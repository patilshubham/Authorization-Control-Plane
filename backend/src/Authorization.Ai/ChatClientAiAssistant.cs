using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Authorization.Ai.Providers;
using Microsoft.Extensions.Logging;

namespace Authorization.Ai;

/// <summary>
/// Live implementation of <see cref="IAiAssistant"/> backed by an OpenAI-compatible chat model
/// (GitHub Models, an OpenAI-compatible endpoint, or Azure OpenAI). Prompts constrain the model
/// to emit strict JSON in the exact shape the rest of the system consumes; parsing degrades
/// gracefully so a malformed model response never crashes the request. All output is advisory
/// and is validated again by the controller before it reaches the client.
/// </summary>
internal sealed class ChatClientAiAssistant : IAiAssistant
{
    public const string HttpClientName = "Authorization.Ai.ChatClient";

    // A valid, empty condition group used when a draft cannot be parsed.
    private const string EmptyConditionGroup = "{\"match\":\"all\",\"conditions\":[]}";

    // Sourced from the mirrored operator list so the prompt cannot drift from the engine.
    private static readonly string SupportedOperators = AiPolicyOperators.CsvList;

    private readonly IChatCompletionClient client;
    private readonly AiOptions options;
    private readonly ILogger<ChatClientAiAssistant> logger;

    public ChatClientAiAssistant(IChatCompletionClient client, AiOptions options, ILogger<ChatClientAiAssistant> logger)
    {
        this.client = client;
        this.options = options;
        this.logger = logger;
    }

    public async Task<PolicyDraftResult> DraftPolicyAsync(PolicyDraftRequest request, CancellationToken cancellationToken)
    {
        string systemPrompt = BuildPolicySystemPrompt(request.KnownPermissionKeys);
        string content = await client.CompleteAsync(systemPrompt, request.Instruction, cancellationToken, temperature: options.Features.PolicyAuthoring.Temperature);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(JsonText.FirstObject(content));
            JsonElement root = doc.RootElement;

            // The model is asked for { "summary": ..., "conditions": { match, conditions:[...] } },
            // but tolerate it returning the condition document directly.
            JsonElement conditionsElement;
            string summary;
            if (root.TryGetProperty("conditions", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object)
            {
                conditionsElement = nested;
                summary = ReadString(root, "summary") ?? DefaultSummary(request.Instruction);
            }
            else
            {
                conditionsElement = root;
                summary = DefaultSummary(request.Instruction);
            }

            if (!IsConditionGroup(conditionsElement))
            {
                return Fallback(request.Instruction, "The assistant response did not contain a usable condition group.");
            }

            return new PolicyDraftResult(
                ConditionsJson: conditionsElement.GetRawText(),
                Summary: summary,
                Warnings: [],
                // Prefer the model's explicit effect; fall back to the instruction's intent so a
                // "deny …" request is never silently drafted as an ALLOW policy.
                SuggestedEffect: ReadEffect(root) ?? PolicyEffectInference.FromInstruction(request.Instruction));
        }
        catch (JsonException)
        {
            return Fallback(request.Instruction, "The assistant returned a draft that could not be parsed.");
        }
    }

    public async Task<DecisionExplanationResult> ExplainDecisionAsync(DecisionExplanationRequest request, CancellationToken cancellationToken)
    {
        string systemPrompt = BuildExplainSystemPrompt();
        string userPrompt = BuildExplainUserPrompt(request);
        string content = await client.CompleteAsync(systemPrompt, userPrompt, cancellationToken, temperature: options.Features.DecisionExplainer.Temperature);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(JsonText.FirstObject(content));
            JsonElement root = doc.RootElement;

            string narrative = ReadString(root, "narrative") ?? content.Trim();
            IReadOnlyList<string> remediation = request.Allowed ? [] : ReadStringList(root, "remediation");
            return new DecisionExplanationResult(narrative, remediation);
        }
        catch (JsonException)
        {
            // The narrative is plain language, so a non-JSON reply is still usable as-is.
            return new DecisionExplanationResult(content.Trim(), []);
        }
    }

    public async Task<ImpactNarrationResult> NarrateImpactAsync(ImpactNarrationRequest request, CancellationToken cancellationToken)
    {
        // The no-impact and no-traffic cases are the most dangerous to leave to a language model:
        // an incorrect "N outcomes change" claim on a change-review surface is misleading. These
        // cases are fully determined by the deterministic counts, so compose them directly and
        // skip the model entirely.
        if (request.EvaluatedCount == 0 || (request.AllowToDenyCount == 0 && request.DenyToAllowCount == 0))
        {
            return new ImpactNarrationResult(ComposeDeterministicSummary(request));
        }

        string systemPrompt = BuildImpactSystemPrompt();
        string userPrompt = BuildImpactUserPrompt(request);
        string content = await client.CompleteAsync(systemPrompt, userPrompt, cancellationToken, temperature: options.Features.ImpactAnalysis.Temperature);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(JsonText.FirstObject(content));
            string? summary = ReadString(doc.RootElement, "summary");
            return new ImpactNarrationResult(string.IsNullOrWhiteSpace(summary) ? content.Trim() : summary.Trim());
        }
        catch (JsonException)
        {
            // The summary is plain language, so a non-JSON reply is still usable as-is.
            return new ImpactNarrationResult(content.Trim());
        }
    }

    public async Task<ConfigAdvisorResult> SummarizeFindingsAsync(ConfigAdvisorRequest request, CancellationToken cancellationToken)
    {
        // With nothing to explain there is no value in calling the model. Return a clean, grounded
        // "all healthy" summary so the UI can render a consistent empty state.
        if (request.Findings.Count == 0)
        {
            return new ConfigAdvisorResult("No governance issues were found. The configuration looks healthy.", []);
        }

        string systemPrompt = BuildAdvisorSystemPrompt();
        string userPrompt = BuildAdvisorUserPrompt(request);
        string content = await client.CompleteAsync(systemPrompt, userPrompt, cancellationToken, temperature: options.Features.ConfigAdvisor.Temperature);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(JsonText.FirstObject(content));
            JsonElement root = doc.RootElement;

            string? summary = ReadString(root, "summary");
            List<ConfigFindingSuggestion> suggestions = ReadSuggestions(root, request);

            return new ConfigAdvisorResult(
                string.IsNullOrWhiteSpace(summary) ? content.Trim() : summary.Trim(),
                suggestions);
        }
        catch (JsonException)
        {
            // A non-JSON reply is still usable as an overall narrative; per-finding fixes are simply
            // omitted rather than fabricated, keeping the deterministic findings authoritative.
            return new ConfigAdvisorResult(content.Trim(), []);
        }
    }

    // Reads the model's per-finding suggestions, keeping only entries whose id matches a real,
    // deterministic finding so the model can never introduce a suggestion for an invented issue.
    private static List<ConfigFindingSuggestion> ReadSuggestions(JsonElement root, ConfigAdvisorRequest request)
    {
        var suggestions = new List<ConfigFindingSuggestion>();
        if (!root.TryGetProperty("suggestions", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return suggestions;
        }

        var knownIds = new HashSet<string>(request.Findings.Select(finding => finding.Id), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? id = ReadString(item, "id");
            string? fix = ReadString(item, "suggestedFix");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(fix)
                || !knownIds.Contains(id) || !seen.Add(id))
            {
                continue;
            }

            suggestions.Add(new ConfigFindingSuggestion(id, fix.Trim()));
        }

        return suggestions;
    }

    private static string BuildAdvisorSystemPrompt()
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are a senior authorization-governance expert and security architect reviewing configuration findings for an authorization control plane. The findings were detected deterministically by the system; an administrator wants your expert interpretation, not a restatement of the data.");
        builder.AppendLine("The findings are ground truth: never invent, merge, drop, or re-score them, and never reference a finding id that is not in the input. Cite only the real entity keys and attributes that appear in the findings.");
        builder.AppendLine("Respond with a single JSON object and nothing else: { \"summary\": string, \"suggestions\": [ { \"id\": string, \"suggestedFix\": string } ] }.");
        builder.AppendLine();
        builder.AppendLine("Write \"summary\" as GitHub-flavored Markdown that reads like an expert review. Use exactly these five second-level headings, in this order, each on its own line:");
        builder.AppendLine("## Executive Summary");
        builder.AppendLine("## Key Observations");
        builder.AppendLine("## Potential Impact");
        builder.AppendLine("## Recommended Actions");
        builder.AppendLine("## Overall Assessment");
        builder.AppendLine();
        builder.AppendLine("Section guidance:");
        builder.AppendLine("- Executive Summary: 1-2 sentences synthesizing what the findings collectively indicate and whether they point to a systemic configuration/integration issue or isolated defects. Do NOT open with a bare count like \"There are 5 low-severity issues\".");
        builder.AppendLine("- Key Observations: bullet points (each line starting with \"- \") naming the dominant patterns, likely common root causes, recurring attributes, and repeated policy-design patterns. For example, several policies depending on context attributes never present in decision evaluations indicate an integration/instrumentation gap between policy definitions and runtime authorization requests. Name the specific roles, policies, and attributes involved.");
        builder.AppendLine("- Potential Impact: bullet points on the practical effect on policy effectiveness, decision accuracy, governance, maintainability, and future scalability.");
        builder.AppendLine("- Recommended Actions: bullet points ordered most-effective-first; begin each with a bold priority label such as \"**Priority 1 —**\". Fix root causes before symptoms (e.g., validate the request payloads consuming applications send before editing policy logic) and prioritize by severity. Include preventive actions (payload validation, monitoring, testing, policy linting, developer guidance).");
        builder.AppendLine("- Overall Assessment: a single line beginning with a bold health rating — one of **Healthy**, **Needs attention**, or **At risk** — followed by a concise justification.");
        builder.AppendLine("Frame risk in business terms, not only technical terms. Be specific, synthesize insight, and keep it scannable: short sentences and bullets over long paragraphs.");
        builder.AppendLine();
        builder.AppendLine("suggestions: one entry per input finding, using its exact id. suggestedFix is a single concrete, actionable sentence describing the fix (do not restate the finding). Do not add any text outside the JSON object.");
        return builder.ToString();
    }

    private static string BuildAdvisorUserPrompt(ConfigAdvisorRequest request)
    {
        // Pre-computed aggregates travel with the raw findings so the model can synthesize patterns
        // (severity mix, recurring finding kinds) reliably instead of re-deriving them from the list.
        var facts = new
        {
            applicationId = request.ApplicationId,
            totals = new
            {
                findings = request.Findings.Count,
                high = request.Findings.Count(finding => string.Equals(finding.Severity, "HIGH", StringComparison.OrdinalIgnoreCase)),
                medium = request.Findings.Count(finding => string.Equals(finding.Severity, "MEDIUM", StringComparison.OrdinalIgnoreCase)),
                low = request.Findings.Count(finding => string.Equals(finding.Severity, "LOW", StringComparison.OrdinalIgnoreCase)),
            },
            byKind = request.Findings
                .GroupBy(finding => finding.Kind, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ToDictionary(group => group.Key, group => group.Count()),
            findings = request.Findings.Select(finding => new
            {
                id = finding.Id,
                kind = finding.Kind,
                severity = finding.Severity,
                title = finding.Title,
                detail = finding.Detail,
                entityType = finding.EntityType,
                entityKey = finding.EntityKey,
            }),
        };

        return JsonSerializer.Serialize(facts);
    }

    // Deterministic summary for cases where a language model adds risk but no value: no traffic to
    // evaluate, or no outcomes change. Grounded entirely in the shadow-evaluated counts.
    private static string ComposeDeterministicSummary(ImpactNarrationRequest request)
    {
        var summary = new StringBuilder();
        summary.Append("Publishing ").Append(request.PolicyKey)
            .Append(" (").Append(request.Effect).Append(") ");

        if (request.EvaluatedCount == 0)
        {
            summary.Append("has no representative traffic to evaluate yet, so its impact cannot be measured. ")
                .Append("Simulate a few requests for this resource and action first.");
            return summary.ToString();
        }

        summary.Append("changes no outcomes across ").Append(request.EvaluatedCount)
            .Append(request.EvaluatedCount == 1 ? " evaluated request." : " evaluated requests.");

        if (!request.SampledFromHistory)
        {
            summary.Append(" No recorded decisions were available, so this used current assignments with empty context; treat it as a lower bound.");
        }

        return summary.ToString();
    }

    public async Task<AccessSearchPlanResult> PlanAccessSearchAsync(AccessSearchPlanRequest request, CancellationToken cancellationToken)
    {
        string systemPrompt = BuildAccessSearchSystemPrompt();
        string content = await CompleteAccessSearchWithDownshiftAsync(systemPrompt, request, cancellationToken);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(JsonText.FirstObject(content));
            JsonElement root = doc.RootElement;

            string entity = ReadString(root, "entity")?.Trim() ?? string.Empty;
            List<AccessSearchPlanFilter> filters = ReadPlanFilters(root);
            string? explanation = ReadString(root, "explanation")?.Trim();
            string? aggregate = ReadString(root, "aggregate")?.Trim();
            aggregate = string.Equals(aggregate, "count", StringComparison.OrdinalIgnoreCase) ? "count" : null;
            string? groupBy = ReadString(root, "groupBy")?.Trim();
            if (string.IsNullOrWhiteSpace(groupBy))
            {
                groupBy = null;
            }

            string? include = ReadString(root, "include")?.Trim();
            if (string.IsNullOrWhiteSpace(include))
            {
                include = null;
            }

            string? groupByField = ReadString(root, "groupByField")?.Trim();
            if (string.IsNullOrWhiteSpace(groupByField))
            {
                groupByField = null;
            }

            int? limit = null;
            if (root.TryGetProperty("limit", out JsonElement limitElement))
            {
                if (limitElement.ValueKind == JsonValueKind.Number && limitElement.TryGetInt32(out int limitNumber) && limitNumber > 0)
                {
                    limit = limitNumber;
                }
                else if (limitElement.ValueKind == JsonValueKind.String
                    && int.TryParse(limitElement.GetString(), out int limitParsed) && limitParsed > 0)
                {
                    limit = limitParsed;
                }
            }

            string? absentRelationship = ReadString(root, "absentRelationship")?.Trim();
            if (string.IsNullOrWhiteSpace(absentRelationship))
            {
                absentRelationship = null;
            }

            string? includeChild = ReadString(root, "includeChild")?.Trim();
            if (string.IsNullOrWhiteSpace(includeChild))
            {
                includeChild = null;
            }

            string? includeGrandchild = ReadString(root, "includeGrandchild")?.Trim();
            if (string.IsNullOrWhiteSpace(includeGrandchild))
            {
                includeGrandchild = null;
            }

            string? presentRelationship = ReadString(root, "presentRelationship")?.Trim();
            if (string.IsNullOrWhiteSpace(presentRelationship))
            {
                presentRelationship = null;
            }

            string? groupBySecondaryField = ReadString(root, "groupBySecondaryField")?.Trim();
            if (string.IsNullOrWhiteSpace(groupBySecondaryField))
            {
                groupBySecondaryField = null;
            }

            // When the model cannot map the question it should still offer answerable, schema-grounded
            // follow-ups. These are natural-language questions only — never executed directly.
            List<string> suggestions = ReadStringList(root, "suggestions")
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Take(5)
                .ToList();

            return new AccessSearchPlanResult(
                entity, filters, explanation, aggregate, groupBy, include,
                suggestions.Count > 0 ? suggestions : null,
                groupByField,
                limit,
                absentRelationship,
                includeChild,
                presentRelationship,
                groupBySecondaryField,
                includeGrandchild);
        }
        catch (JsonException)
        {
            // A non-JSON reply cannot be executed safely. Return an empty plan so the controller
            // responds with a validation error rather than guessing.
            logger.LogWarning("Access-search planning returned non-JSON output; treating as no plan.");
            return new AccessSearchPlanResult(string.Empty, [], null);
        }
    }

    // Reads the model's planned filters. Values are always treated as plain literals; the API layer
    // validates every field against the closed schema and executes with parameterized queries, so a
    // filter can never smuggle SQL or reference a field that does not exist.
    private static List<AccessSearchPlanFilter> ReadPlanFilters(JsonElement root)
    {
        var filters = new List<AccessSearchPlanFilter>();
        if (!root.TryGetProperty("filters", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return filters;
        }

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? field = ReadString(item, "field")?.Trim();
            string? op = ReadString(item, "operator")?.Trim();
            string? value = ReadString(item, "value")?.Trim();
            if (string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            filters.Add(new AccessSearchPlanFilter(
                field,
                string.IsNullOrWhiteSpace(op) ? "eq" : op,
                value));
        }

        return filters;
    }

    private static string BuildAccessSearchSystemPrompt()
    {
        var builder = new StringBuilder();
        builder.AppendLine("You translate a natural-language access question into a strict, closed query specification for an authorization control plane. You never write SQL and never invent field names or values.");
        builder.AppendLine("Respond with a single JSON object and nothing else: { \"entity\": string, \"filters\": [ { \"field\": string, \"operator\": string, \"value\": string } ], \"aggregate\": string, \"groupBy\": string, \"groupByField\": string, \"groupBySecondaryField\": string, \"limit\": number, \"presentRelationship\": string, \"absentRelationship\": string, \"include\": string, \"includeChild\": string, \"includeGrandchild\": string, \"explanation\": string, \"suggestions\": [string] }.");
        builder.AppendLine("You are given: `hierarchy` (how the entities relate), `schema` (every allowed entity with a description, its relationships, and a `fields` list where each field has a `type`, an optional allowed `values` domain, and a description), and `vocabulary` (a sample of the real values that exist in this application).");
        builder.AppendLine("entity must be exactly one of the schema entities. Each filter.field must be one of that entity's listed fields — never use a field that is not listed for the chosen entity.");
        builder.AppendLine("Pick the operator from the field's `type`: string/ref → \"eq\", \"neq\", \"contains\", \"in\"; enum/bool → \"eq\", \"neq\", \"in\"; number/date → \"eq\", \"neq\", \"gt\", \"gte\", \"lt\", \"lte\". Operator meanings: \"eq\" (exact, case-insensitive), \"neq\" (not equal), \"contains\" (substring, string fields only), \"in\" (value is a comma-separated set; matches any), \"gt\"/\"gte\"/\"lt\"/\"lte\" (ordered number/date comparisons).");
        builder.AppendLine("For a field of type \"enum\" or \"bool\", the value MUST be one of its listed `values` (e.g. status inactive → status neq ACTIVE, or status in DISABLED,DEPRECATED,ARCHIVED; a DENY policy → effect eq DENY). For a field of type \"ref\", ground the value in the matching vocabulary list. For type \"date\" you may use the literal \"now\" for the current time.");
        builder.AppendLine("Use the hierarchy to pick the right entity: \"who can do X\" → subject; \"which roles grant X\" → role; grants/expiry → assignment (expired = state eq EXPIRED or validUntil lt now); policies → policy.");
        builder.AppendLine("Set \"aggregate\" to \"count\" only when the user asks how many / for a total / a number of matching records; otherwise omit it to return the matching rows.");
        builder.AppendLine("Set \"groupBy\" to one of the chosen entity's listed relationships only when the user asks for a per-group count (e.g. \"how many permissions does each role grant\"); otherwise omit it. groupBy takes precedence over aggregate.");
        builder.AppendLine("Set \"groupByField\" to a low-cardinality attribute of the chosen entity for a breakdown/histogram by that attribute — role or permission: riskLevel or status; policy: effect or state (e.g. \"how many roles by risk level\", \"break down policies by effect\"); otherwise omit it. groupBy (relationship) takes precedence over groupByField, which takes precedence over aggregate.");
        builder.AppendLine("Set \"groupBySecondaryField\" (only together with groupByField, and a different groupable field of the same entity) for a two-level cross-tab breakdown — the primary field's buckets each split by the secondary (e.g. \"roles by risk level then status\" → groupByField riskLevel, groupBySecondaryField status; \"policies by effect and state\" → groupByField effect, groupBySecondaryField state). Omit it for a single-level breakdown.");
        builder.AppendLine("Set \"limit\" to a positive integer only for a top-N or \"show N\" request (e.g. \"top 5 roles by permission count\", \"show 10 policies\"); for a group it returns the N highest-count groups. Omit it otherwise.");
        builder.AppendLine("Set \"absentRelationship\" to one of the chosen entity's listed relationships for a \"has none\"/absence question — the records that have ZERO of that relationship (e.g. \"roles with no assignments\" → entity role, absentRelationship assignments; \"unused permissions\"/\"permissions granted by no role\" → entity permission, absentRelationship roles; \"roles that grant no permissions\" → entity role, absentRelationship permissions). Omit it otherwise. absentRelationship takes precedence over groupBy, groupByField and aggregate; include still takes precedence over it.");
        builder.AppendLine("Set \"presentRelationship\" to one of the chosen entity's listed relationships for a \"has\" question, and combine it with absentRelationship for a compound \"has A but no B\" question (e.g. \"roles that grant permissions but have no assignments\" → entity role, presentRelationship permissions, absentRelationship assignments; \"permissions granted by a role but with no policy\" → entity permission, presentRelationship roles, absentRelationship policies; \"tenants with policies but no active users\" → entity tenant, presentRelationship policies, absentRelationship assignments). Presence/absence is supported for role, permission, policy, application and tenant. Omit it otherwise.");
        builder.AppendLine("Some entities expose derived \"problem\" relationships for governance hygiene — application: unusedPermissions (permissions no role grants) and unassignedRoles (active roles no one is assigned). Use them like any relationship: \"applications with unused permissions\" → entity application, presentRelationship unusedPermissions; \"how many unassigned roles per application\" → entity application, groupBy unassignedRoles; \"show each application's unused permissions\" → entity application, include unusedPermissions.");
        builder.AppendLine("Set \"include\" to one of the chosen entity's listed relationships only when the user wants the related records shown alongside each result (e.g. \"show the pricing-admin role and its permissions\"); otherwise omit it. include takes precedence over groupBy and aggregate.");
        builder.AppendLine("Set \"includeChild\" only together with \"include\", for a two-level chain: it must be a relationship of the include's target (child) entity, and expands each child one more hop (e.g. \"roles, their permissions, and each permission's policies\" → include permissions, includeChild policies; \"a subject, their roles and each role's permissions\" → include roles, includeChild permissions). Omit it for a single-level include.");
        builder.AppendLine("Set \"includeGrandchild\" only together with \"includeChild\", for a three-level chain: it must be a relationship of the includeChild's target (grandchild) entity, and expands each grandchild one more hop (e.g. \"tenants, their roles, each role's permissions, and each permission's policies\" → entity tenant, include roles, includeChild permissions, includeGrandchild policies). Omit it for a two-level (or single-level) include.");
        builder.AppendLine("Only use values that appear in the provided vocabulary or a field's allowed `values`. If the question mentions something not present, omit that filter rather than guessing.");
        builder.AppendLine("If — and only if — the question cannot be mapped to any entity/field, return an empty entity string, an empty filters array, and populate \"suggestions\" with 3 to 5 concrete, fully answerable questions phrased in plain English, each grounded in the actual entities, fields, allowed values, and vocabulary above (e.g. specific role keys, real permission keys, enum values). Make them specific — never generic. When you can map the question, leave \"suggestions\" empty.");
        builder.AppendLine("Be precise and minimal: include only the filters needed to answer the question.");
        return builder.ToString();
    }

    // Progressive-detail levels used when the provider rejects the payload as too large (HTTP 413):
    // 0 = full context, 1 = drop the grounding vocabulary, 2 = schema names + question only. Correctness
    // is unaffected — the executor still validates every value against the live data.
    private const int MaxDownshiftLevel = 2;

    // Sends the planning prompt, shrinking the payload one detail level at a time whenever the provider
    // reports the request is too large. Any other failure propagates unchanged.
    private async Task<string> CompleteAccessSearchWithDownshiftAsync(
        string systemPrompt,
        AccessSearchPlanRequest request,
        CancellationToken cancellationToken)
    {
        for (int detailLevel = 0; ; detailLevel++)
        {
            try
            {
                string userPrompt = BuildAccessSearchUserPrompt(request, detailLevel);
                return await client.CompleteAsync(systemPrompt, userPrompt, cancellationToken, temperature: options.Features.AccessSearch.Temperature);
            }
            catch (AiPayloadTooLargeException) when (detailLevel < MaxDownshiftLevel)
            {
                logger.LogWarning(
                    "Access-search prompt exceeded the provider payload limit at detail level {DetailLevel}; retrying with less context.",
                    detailLevel);
            }
        }
    }

    private static string BuildAccessSearchUserPrompt(AccessSearchPlanRequest request, int detailLevel)
    {
        bool includeVocabulary = detailLevel < 1;
        bool includeDetail = detailLevel < 2;

        var facts = new
        {
            question = request.Question,
            hierarchy = includeDetail ? request.Hierarchy : null,
            schema = request.Schema.Select(entity => new
            {
                entity = entity.Entity,
                description = includeDetail ? entity.Description : null,
                relationships = includeDetail && entity.Relationships is { Count: > 0 } ? entity.Relationships : null,
                fields = BuildFieldFacts(entity, includeDetail),
            }),
            // A bounded grounding sample. The executor validates every value against the live data
            // regardless, so a smaller sample keeps the request within provider size limits without
            // weakening the closed-schema guarantee. Dropped entirely at detail level >= 1.
            vocabulary = includeVocabulary
                ? new
                {
                    permissionKeys = Sample(request.Vocabulary.PermissionKeys),
                    resources = Sample(request.Vocabulary.Resources),
                    actions = Sample(request.Vocabulary.Actions),
                    roleKeys = Sample(request.Vocabulary.RoleKeys),
                    resourceIds = Sample(request.Vocabulary.ResourceIds),
                }
                : null,
        };

        return JsonSerializer.Serialize(facts, AccessSearchPromptOptions);
    }

    // Builds the per-field facts. At full detail each field carries its type, allowed values and
    // description; when downshifting we send only field names. Operators are always derived from the
    // type per the system prompt, so they are never sent on the wire.
    private static List<object> BuildFieldFacts(AccessSearchEntitySchema entity, bool includeDetail)
    {
        if (includeDetail && entity.FieldSchemas is { Count: > 0 })
        {
            return entity.FieldSchemas.Select(f => (object)new
            {
                name = f.Name,
                type = f.Type,
                values = f.Values is { Count: > 0 } ? f.Values : null,
                description = f.Description,
            }).ToList();
        }

        if (entity.FieldSchemas is { Count: > 0 })
        {
            return entity.FieldSchemas.Select(f => (object)new { name = f.Name }).ToList();
        }

        return entity.Fields.Select(name => (object)new { name }).ToList();
    }

    // Caps how many grounded values of each kind are sent to the model. Enough to recognise the
    // application's naming patterns while keeping the prompt small.
    private const int VocabularySampleLimit = 50;

    private static readonly JsonSerializerOptions AccessSearchPromptOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static IReadOnlyList<string>? Sample(IReadOnlyList<string> values) =>
        values.Count == 0 ? null
        : values.Count <= VocabularySampleLimit ? values
        : values.Take(VocabularySampleLimit).ToList();

    public async Task<SodRuleDraftResult> DraftSodRuleAsync(SodRuleDraftRequest request, CancellationToken cancellationToken)
    {
        string systemPrompt = BuildSodRuleSystemPrompt();
        string userPrompt = BuildSodRuleUserPrompt(request);
        string content = await client.CompleteAsync(systemPrompt, userPrompt, cancellationToken, temperature: options.Features.SodAnalysis.Temperature);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(JsonText.FirstObject(content));
            JsonElement root = doc.RootElement;

            SodMatcherDraft matcherA = ReadMatcher(root, "matcherA");
            SodMatcherDraft matcherB = ReadMatcher(root, "matcherB");
            string name = ReadString(root, "name")?.Trim() is { Length: > 0 } n ? n : "Separation-of-duties rule";
            string rationale = ReadString(root, "rationale")?.Trim() ?? string.Empty;
            string severity = NormalizeSeverity(ReadString(root, "severity"));
            List<string> warnings = ReadStringList(root, "warnings").ToList();

            if (IsEmptyMatcher(matcherA) || IsEmptyMatcher(matcherB))
            {
                warnings.Add("The draft is missing one or both sides of the rule; edit the matchers before saving.");
            }

            return new SodRuleDraftResult(name, rationale, severity, matcherA, matcherB, warnings);
        }
        catch (JsonException)
        {
            // A non-JSON reply cannot be persisted safely. Return an empty draft with a warning so the
            // controller responds with a validation error rather than guessing.
            logger.LogWarning("SoD rule drafting returned non-JSON output; treating as no draft.");
            return new SodRuleDraftResult(
                "Separation-of-duties rule",
                string.Empty,
                "HIGH",
                new SodMatcherDraft(null, null, null),
                new SodMatcherDraft(null, null, null),
                ["The model did not return a usable draft; author the rule manually."]);
        }
    }

    // Reads one matcher object. Values are plain literals grounded in the supplied vocabulary; the API
    // layer validates that referenced permission keys / resources / actions actually exist before saving.
    private static SodMatcherDraft ReadMatcher(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement matcher) || matcher.ValueKind != JsonValueKind.Object)
        {
            return new SodMatcherDraft(null, null, null);
        }

        string? permissionKey = ReadString(matcher, "permissionKey")?.Trim();
        string? resource = ReadString(matcher, "resource")?.Trim();
        string? action = ReadString(matcher, "action")?.Trim();

        return new SodMatcherDraft(
            string.IsNullOrWhiteSpace(permissionKey) ? null : permissionKey,
            string.IsNullOrWhiteSpace(resource) ? null : resource,
            string.IsNullOrWhiteSpace(action) ? null : action);
    }

    private static bool IsEmptyMatcher(SodMatcherDraft matcher) =>
        matcher.PermissionKey is null && matcher.Resource is null && matcher.Action is null;

    private static string NormalizeSeverity(string? severity)
    {
        string value = (severity ?? string.Empty).Trim().ToUpperInvariant();
        return value is "LOW" or "MEDIUM" or "HIGH" or "CRITICAL" ? value : "HIGH";
    }

    private static string BuildSodRuleSystemPrompt()
    {
        var builder = new StringBuilder();
        builder.AppendLine("You draft a Separation-of-Duties (SoD) rule for an authorization control plane from a natural-language instruction. An SoD rule names a pair of permissions that no single role or user should ever hold together (a toxic combination).");
        builder.AppendLine("Respond with a single JSON object and nothing else: { \"name\": string, \"rationale\": string, \"severity\": string, \"matcherA\": { \"permissionKey\"?: string, \"resource\"?: string, \"action\"?: string }, \"matcherB\": { \"permissionKey\"?: string, \"resource\"?: string, \"action\"?: string }, \"warnings\": [string] }.");
        builder.AppendLine("Each matcher describes one side of the toxic combination. Prefer a single permissionKey when one from the vocabulary matches; otherwise use a resource and/or action pair.");
        builder.AppendLine("Only use permission keys, resources, and actions that appear in the provided vocabulary. Never invent a permission, resource, or action. If a side cannot be grounded in the vocabulary, leave its fields empty and add a warning.");
        builder.AppendLine("severity must be one of LOW, MEDIUM, HIGH, CRITICAL. Default to HIGH for financial or publishing conflicts.");
        builder.AppendLine("matcherA and matcherB must describe two DIFFERENT permissions.");
        return builder.ToString();
    }

    private static string BuildSodRuleUserPrompt(SodRuleDraftRequest request)
    {
        var facts = new
        {
            applicationId = request.ApplicationId,
            instruction = request.Instruction,
            vocabulary = new
            {
                permissionKeys = request.KnownPermissionKeys,
                resources = request.KnownResources,
                actions = request.KnownActions,
            },
        };

        return JsonSerializer.Serialize(facts);
    }

    public async Task<AccessReviewSummaryResult> SummarizeAccessReviewAsync(AccessReviewSummaryRequest request, CancellationToken cancellationToken)
    {
        // With no active access there is nothing to summarize; skip the model call.
        if (request.Items.Count == 0)
        {
            return new AccessReviewSummaryResult($"{request.SubjectLabel} holds no active access to review.", []);
        }

        string systemPrompt = BuildAccessReviewSystemPrompt();
        string userPrompt = BuildAccessReviewUserPrompt(request);
        string content = await client.CompleteAsync(systemPrompt, userPrompt, cancellationToken, temperature: options.Features.AccessCertification.Temperature);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(JsonText.FirstObject(content));
            JsonElement root = doc.RootElement;

            string? summary = ReadString(root, "summary");
            List<AccessReviewItemRationale> rationales = ReadReviewRationales(root, request);

            return new AccessReviewSummaryResult(
                string.IsNullOrWhiteSpace(summary) ? content.Trim() : summary.Trim(),
                rationales);
        }
        catch (JsonException)
        {
            // A non-JSON reply is still usable as an overall narrative; per-item rationales are simply
            // omitted rather than fabricated, keeping the deterministic recommendations authoritative.
            return new AccessReviewSummaryResult(content.Trim(), []);
        }
    }

    // Reads the model's per-item rationales, keeping only entries whose id matches a real reviewed
    // item so the model can never introduce a rationale for an invented grant.
    private static List<AccessReviewItemRationale> ReadReviewRationales(JsonElement root, AccessReviewSummaryRequest request)
    {
        var rationales = new List<AccessReviewItemRationale>();
        if (!root.TryGetProperty("rationales", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return rationales;
        }

        var knownIds = new HashSet<string>(request.Items.Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? id = ReadString(item, "id");
            string? rationale = ReadString(item, "rationale");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(rationale)
                || !knownIds.Contains(id) || !seen.Add(id))
            {
                continue;
            }

            rationales.Add(new AccessReviewItemRationale(id, rationale.Trim()));
        }

        return rationales;
    }

    private static string BuildAccessReviewSystemPrompt()
    {
        var builder = new StringBuilder();
        builder.AppendLine("You assist an access-certification (recertification) review in an authorization control plane. A reviewer must decide, for each grant a user holds, whether to keep or revoke it.");
        builder.AppendLine("Every fact — roles, effective permissions, last-used signal, dormancy, peer comparison, and a keep/revoke/review recommendation — was computed deterministically by the system. Treat them as ground truth: never invent grants, never change a recommendation, and never reference any item id that is not in the input.");
        builder.AppendLine("The input contains NO personal data: the user is identified only by a pseudonymous label and items by opaque ids. Do not ask for or infer any real identity.");
        builder.AppendLine("Respond with a single JSON object and nothing else: { \"summary\": string, \"rationales\": [ { \"id\": string, \"rationale\": string } ] }.");
        builder.AppendLine("summary: 2-3 plain-language sentences that state how many grants are under review, how many are recommended keep/revoke/review, and which anomalies (privileged, dormant, or outlier grants) deserve attention first.");
        builder.AppendLine("rationales: one entry per input item, using its exact id. rationale is a single concise sentence explaining why the recommendation was made, grounded only in the supplied signals.");
        builder.AppendLine("Be concise, neutral, and specific. Do not add commentary outside the JSON object.");
        return builder.ToString();
    }

    private static string BuildAccessReviewUserPrompt(AccessReviewSummaryRequest request)
    {
        var facts = new
        {
            subject = request.SubjectLabel,
            itemCount = request.ItemCount,
            keepCount = request.KeepCount,
            revokeCount = request.RevokeCount,
            reviewCount = request.ReviewCount,
            items = request.Items.Select(item => new
            {
                id = item.Id,
                applicationId = item.ApplicationId,
                roleKey = item.RoleKey,
                roleName = item.RoleName,
                privileged = item.Privileged,
                riskLevel = item.RiskLevel,
                recommendation = item.Recommendation,
                recommendationReason = item.RecommendationReason,
                lastUsedDaysAgo = item.LastUsedDaysAgo,
                dormant = item.Dormant,
                peerCount = item.PeerCount,
                permissions = item.Permissions,
            }),
        };

        return JsonSerializer.Serialize(facts);
    }

    public async Task<AuditNarrationResult> NarrateAuditAsync(AuditNarrationRequest request, CancellationToken cancellationToken)
    {
        // Nothing happened in the window; there is nothing to narrate, so skip the model call.
        if (request.Events.Count == 0)
        {
            return new AuditNarrationResult("No audit activity was recorded in the selected window.", []);
        }

        string systemPrompt = BuildAuditSystemPrompt();
        string userPrompt = BuildAuditUserPrompt(request);
        string content = await client.CompleteAsync(systemPrompt, userPrompt, cancellationToken, temperature: options.Features.AuditNarrative.Temperature);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(JsonText.FirstObject(content));
            JsonElement root = doc.RootElement;

            string? summary = ReadString(root, "summary");
            List<AuditNarrativeSection> sections = ReadAuditSections(root, request);

            return new AuditNarrationResult(
                string.IsNullOrWhiteSpace(summary) ? content.Trim() : summary.Trim(),
                sections);
        }
        catch (JsonException)
        {
            // A non-JSON reply is still usable as an overall narrative; grounded sections are simply
            // omitted rather than fabricated, keeping the deterministic events authoritative.
            return new AuditNarrationResult(content.Trim(), []);
        }
    }

    // Reads the model's sections, keeping only event ids that match a real event so the model can
    // never cite an event it was not given.
    private static List<AuditNarrativeSection> ReadAuditSections(JsonElement root, AuditNarrationRequest request)
    {
        var sections = new List<AuditNarrativeSection>();
        if (!root.TryGetProperty("sections", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return sections;
        }

        var knownIds = new HashSet<string>(request.Events.Select(e => e.EventId), StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? heading = ReadString(item, "heading");
            string? detail = ReadString(item, "detail");
            if (string.IsNullOrWhiteSpace(heading) && string.IsNullOrWhiteSpace(detail))
            {
                continue;
            }

            var eventIds = new List<string>();
            if (item.TryGetProperty("eventIds", out JsonElement ids) && ids.ValueKind == JsonValueKind.Array)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonElement id in ids.EnumerateArray())
                {
                    string? value = id.ValueKind == JsonValueKind.String ? id.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(value) && knownIds.Contains(value) && seen.Add(value))
                    {
                        eventIds.Add(value);
                    }
                }
            }

            sections.Add(new AuditNarrativeSection(
                (heading ?? string.Empty).Trim(),
                (detail ?? string.Empty).Trim(),
                eventIds));
        }

        return sections;
    }

    private static string BuildAuditSystemPrompt()
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are a compliance-evidence assistant for an authorization control plane. An auditor needs an auditor-ready narrative of the administrative changes in a time window (SOX/ISO evidence).");
        builder.AppendLine("Every fact — the events, the grouped counts, and the deny-reason aggregate — was computed deterministically by the system. Treat them as ground truth: never invent an event, a count, or an actor, and never cite an event id that is not in the input.");
        builder.AppendLine("The input contains NO personal data: actors and targets are identified only by pseudonymous labels (e.g. person-1) and events by opaque ids. Refer to actors and targets only by the exact label provided; do not guess real identities.");
        builder.AppendLine("Respond with a single JSON object and nothing else: { \"summary\": string, \"sections\": [ { \"heading\": string, \"detail\": string, \"eventIds\": [string] } ] }.");
        builder.AppendLine("summary: 2-4 plain-language sentences stating how many changes occurred, across how many applications, who was most active, and any notable denial activity.");
        builder.AppendLine("sections: group related changes (for example by change type or application). heading is a short title; detail is 1-3 sentences describing what changed and why; eventIds lists the exact ids of the events that section covers.");
        builder.AppendLine("Be concise, neutral, and specific. Do not add commentary outside the JSON object.");
        return builder.ToString();
    }

    private static string BuildAuditUserPrompt(AuditNarrationRequest request)
    {
        var facts = new
        {
            window = new { from = request.FromUtc, to = request.ToUtc },
            totalEvents = request.TotalEvents,
            byApplication = request.ByApplication.Select(g => new { g.Key, g.Count }),
            byActor = request.ByActor.Select(g => new { g.Key, g.Count }),
            byEventType = request.ByEventType.Select(g => new { g.Key, g.Count }),
            topDenyReasons = request.TopDenyReasons.Select(r => new { r.ApplicationId, r.DenyReason, r.Count }),
            events = request.Events.Select(e => new
            {
                id = e.EventId,
                eventType = e.EventType,
                applicationId = e.ApplicationId,
                actor = e.ActorLabel,
                target = e.TargetLabel,
                timestamp = e.Timestamp,
                reason = e.Reason,
            }),
        };

        return JsonSerializer.Serialize(facts);
    }

    private static string BuildImpactSystemPrompt()

    {
        var builder = new StringBuilder();
        builder.AppendLine("You are a change-review assistant for an authorization control plane. An administrator is about to publish a draft policy and you must summarise its blast radius so a reviewer can approve it with confidence.");
        builder.AppendLine("The counts and the list of affected subjects were computed deterministically by shadow-evaluating the draft; treat them as ground truth. Never invent subjects, counts, or outcomes beyond the supplied facts.");
        builder.AppendLine("Respond with a single JSON object and nothing else: { \"summary\": string }.");
        builder.AppendLine("Number rules — follow these exactly and never deviate:");
        builder.AppendLine("- The number of outcomes that become denied is EXACTLY allowToDenyCount. The number that become allowed is EXACTLY denyToAllowCount. Never state any other number as a count of changed outcomes.");
        builder.AppendLine("- evaluatedCount is only how many requests were examined; it is NOT how many change. Never say evaluatedCount outcomes change, and never use it as the number newly denied or newly allowed.");
        builder.AppendLine("- If allowToDenyCount is 0, do not say anything becomes denied. If denyToAllowCount is 0, do not say anything becomes allowed.");
        builder.AppendLine("- If both allowToDenyCount and denyToAllowCount are 0, you MUST state that publishing changes no outcomes across the evaluated requests (mentioning evaluatedCount is fine as context).");
        builder.AppendLine("summary: 2-4 plain-language sentences. State how many outcomes change and in which direction (newly denied vs newly allowed) using only the counts above, name a few affected subjects when present, and call out clearly when there is no impact. If sampledFromHistory is false, warn that the estimate is a lower bound based on current assignments with no request context.");
        builder.AppendLine("Be concise and neutral. Do not tell the administrator whether to publish; only describe the impact.");
        return builder.ToString();
    }

    private static string BuildImpactUserPrompt(ImpactNarrationRequest request)
    {
        var facts = new
        {
            policyKey = request.PolicyKey,
            effect = request.Effect,
            evaluatedCount = request.EvaluatedCount,
            allowToDenyCount = request.AllowToDenyCount,
            denyToAllowCount = request.DenyToAllowCount,
            sampledFromHistory = request.SampledFromHistory,
            flips = request.Flips.Take(20).Select(flip => new
            {
                subjectEmail = flip.SubjectEmail,
                resourceId = flip.ResourceId,
                action = flip.Action,
                before = flip.Before ? "ALLOW" : "DENY",
                after = flip.After ? "ALLOW" : "DENY",
                reason = flip.Reason,
            }),
        };

        return JsonSerializer.Serialize(facts);
    }

    private PolicyDraftResult Fallback(string instruction, string warning)
    {
        logger.LogWarning("AI policy draft could not be parsed; returning an empty condition group.");
        return new PolicyDraftResult(
            ConditionsJson: EmptyConditionGroup,
            Summary: DefaultSummary(instruction),
            Warnings: [warning + " Start from an empty policy and add conditions manually."]);
    }

    private static string BuildPolicySystemPrompt(IReadOnlyList<string> knownPermissionKeys)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You convert an administrator's plain-language instruction into an authorization policy condition document.");
        builder.AppendLine("Respond with a single JSON object and nothing else. Do not use markdown or code fences.");
        builder.AppendLine("The JSON object must have this shape:");
        builder.AppendLine("{ \"summary\": string, \"effect\": \"ALLOW\"|\"DENY\", \"conditions\": <group> }");
        builder.AppendLine("effect is the policy outcome the instruction asks for: use \"DENY\" when the instruction blocks, forbids, prevents, restricts, or denies access (e.g. 'deny if country is Pakistan'); otherwise use \"ALLOW\". The conditions describe WHEN the effect applies, never the effect itself.");
        builder.AppendLine("A <group> is: { \"match\": \"all\"|\"any\"|\"none\", \"conditions\": [ <node>, ... ] }.");
        builder.AppendLine("\"all\" means every child must match (AND); \"any\" means at least one child (OR); \"none\" means no child may match (NOT).");
        builder.AppendLine("A <node> is either a nested <group> or a leaf: { \"attribute\": string, \"operator\": string, \"value\": string }.");
        builder.AppendLine($"Supported operators: {SupportedOperators}.");
        builder.AppendLine("Attributes reference the request context as \"context.<name>\" (nested paths allowed, e.g. \"context.user.dept\"), the subject's assignment as \"assignment.<name>\", or the evaluation clock as \"system.now\"/\"system.date\"/\"system.time\"/\"system.hour\"/\"system.dayOfWeek\"/\"system.dow\".");
        builder.AppendLine("Operator value rules: 'exists'/'notExists'/'isTrue'/'isFalse' take an empty string value; 'in'/'notIn'/'containsAny'/'containsAll' take a comma-separated list; 'between' takes exactly two comma-separated bounds (min,max, inclusive); 'before'/'after' compare ISO-8601 dates; 'matches'/'notMatches' take a regular expression.");
        builder.AppendLine("If the instruction cannot be expressed, return an empty group: { \"match\": \"all\", \"conditions\": [] }.");

        if (knownPermissionKeys.Count > 0)
        {
            builder.Append("Known permission keys for this application: ");
            builder.AppendLine(string.Join(", ", knownPermissionKeys));
        }

        return builder.ToString();
    }

    private static string BuildExplainSystemPrompt()
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are an access-control debugging analyst. An administrator ran an authorization check and you must explain the outcome like a human expert reviewing an access ticket.");
        builder.AppendLine("The decision was already made by a deterministic policy engine; never contradict or re-decide it.");
        builder.AppendLine("Reason ONLY over the supplied facts and diagnostics. Do not invent roles, permissions, policies, emails, or attributes that are not present in the input.");
        builder.AppendLine("Pinpoint the exact root cause. Use the diagnostics to distinguish between, for example: a misspelled subject email (subjectKnown is false but similarKnownSubjectEmails lists a close match); the subject having no roles; the subject holding roles that do not grant the permission (rolesGrantingPermission lists which roles would); a resource type or action that is not defined (permissionExists is false); a policy that explicitly blocked the request (a relevantPolicies entry with effect DENY and matched true); or a required context attribute that was not supplied (referencedContextAttributes not present in providedContextKeys).");
        builder.AppendLine("When a fact only implies a likely cause (e.g. a probable typo), express it as a likelihood (\"this strongly suggests…\"), not a certainty.");
        builder.AppendLine("Respond with a single JSON object and nothing else: { \"narrative\": string, \"remediation\": string[] }.");
        builder.AppendLine("narrative: 2-4 sentences of grounded analysis explaining precisely why the request was allowed or denied, citing the specific roles/permissions/policies/attributes involved.");
        builder.AppendLine("remediation: if denied, an ordered list of concrete, specific steps an administrator can take to resolve it (reference the actual role/permission/policy/email names from the facts); if allowed, an empty array [].");
        return builder.ToString();
    }

    private static string BuildExplainUserPrompt(DecisionExplanationRequest request)
    {
        var facts = new
        {
            outcome = request.Allowed ? "ALLOWED" : "DENIED",
            denyReason = request.DenyReason,
            subjectType = request.SubjectType,
            subjectEmail = request.SubjectEmail,
            resourceType = request.ResourceType,
            resourceId = request.ResourceId,
            action = request.Action,
            matchedRoles = request.MatchedRoles,
            matchedPermissions = request.MatchedPermissions,
            matchedPolicies = request.MatchedPolicies,
            diagnostics = request.Diagnostics,
        };

        return JsonSerializer.Serialize(facts);
    }

    private static string DefaultSummary(string instruction) =>
        "Draft generated from: " + (instruction.Length <= 200 ? instruction : instruction[..200]);

    private static bool IsConditionGroup(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("conditions", out JsonElement conditions)
        && conditions.ValueKind == JsonValueKind.Array;

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Reads the suggested policy effect, normalising to "ALLOW"/"DENY" and ignoring anything else.
    private static string? ReadEffect(JsonElement element)
    {
        string? raw = ReadString(element, "effect");
        if (string.Equals(raw, "DENY", StringComparison.OrdinalIgnoreCase))
        {
            return "DENY";
        }

        if (string.Equals(raw, "ALLOW", StringComparison.OrdinalIgnoreCase))
        {
            return "ALLOW";
        }

        return null;
    }

    // Reads a remediation field that may be either a JSON array of strings or, for tolerance,
    // a single string. Blank entries are dropped.
    private static IReadOnlyList<string> ReadStringList(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = new List<string>();
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string? text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        items.Add(text.Trim());
                    }
                }
            }

            return items;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string? single = value.GetString();
            return string.IsNullOrWhiteSpace(single) ? [] : [single.Trim()];
        }

        return [];
    }
}

/// <summary>Helpers for coaxing strict JSON out of a model reply that may include stray text.</summary>
internal static class JsonText
{
    /// <summary>
    /// Returns the substring from the first <c>{</c> to the last <c>}</c>, tolerating code fences
    /// or leading/trailing prose. Falls back to the original text when no braces are present.
    /// </summary>
    public static string FirstObject(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        int start = content.IndexOf('{');
        int end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : content;
    }
}
