using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authorization.Infrastructure.RuntimeAuthorization;

public sealed class EfAuthorizationPolicyEngine : IAuthorizationPolicyEngine
{
    /// <summary>
    /// Name of the <see cref="ActivitySource"/> that emits a span per authorization decision.
    /// Register this with the tracing pipeline (AddSource) to capture decision latency.
    /// </summary>
    public const string ActivitySourceName = "Authorization.RuntimeAuthorization";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private readonly AuthorizationDbContext dbContext;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<EfAuthorizationPolicyEngine> logger;

    public EfAuthorizationPolicyEngine(
        AuthorizationDbContext dbContext,
        TimeProvider timeProvider,
        ILogger<EfAuthorizationPolicyEngine>? logger = null)
    {
        this.dbContext = dbContext;
        this.timeProvider = timeProvider;
        this.logger = logger ?? NullLogger<EfAuthorizationPolicyEngine>.Instance;
    }

    public async Task<AuthorizeDecision> AuthorizeAsync(AuthorizeRequest request, CancellationToken cancellationToken = default)
    {
        using Activity? activity = ActivitySource.StartActivity("authorization.decision", ActivityKind.Internal);
        activity?.SetTag("authorization.application_id", request.ApplicationId);
        activity?.SetTag("authorization.resource_type", request.ResourceType);
        activity?.SetTag("authorization.action", request.Action);

        long startTimestamp = Stopwatch.GetTimestamp();
        AuthorizeDecision decision = await AuthorizeInternalAsync(request, cancellationToken);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);

        activity?.SetTag("authorization.allowed", decision.Allowed);
        activity?.SetTag("authorization.deny_reason", decision.DenyReason);
        activity?.SetTag("authorization.duration_ms", elapsed.TotalMilliseconds);

        logger.LogDebug(
            "Authorization decision for application {ApplicationId} resource {ResourceType}:{Action} completed in {ElapsedMs:F2} ms (allowed={Allowed}, denyReason={DenyReason}).",
            request.ApplicationId,
            request.ResourceType,
            request.Action,
            elapsed.TotalMilliseconds,
            decision.Allowed,
            decision.DenyReason);

        return decision;
    }

    private async Task<AuthorizeDecision> AuthorizeInternalAsync(AuthorizeRequest request, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        ApplicationReference? application = await GetApplicationAsync(request.ApplicationId, cancellationToken);

        if (application is null)
        {
            return Deny("APPLICATION_NOT_FOUND");
        }

        PermissionReference? permission = await GetPermissionAsync(application, request.ResourceType, request.Action, cancellationToken);

        if (permission is null)
        {
            return Deny("PERMISSION_NOT_FOUND");
        }

        List<AssignmentEntity> allSubjectAssignments = await dbContext.Assignments
            .AsNoTracking()
            .Where(entity => entity.ApplicationRefId == application.RowId
                && entity.SubjectType == request.SubjectType
                && entity.SubjectEmail == request.SubjectEmail)
            .ToListAsync(cancellationToken);

        List<AssignmentEntity> activeAssignments = allSubjectAssignments
            .Where(entity => entity.State == "ACTIVE"
                && entity.RevokedAt is null
                && entity.ValidFrom <= now
                && (entity.ValidUntil is null || entity.ValidUntil > now))
            .ToList();

        if (activeAssignments.Count == 0)
        {
            // Surface the most informative deny reason when no active assignment exists.
            if (allSubjectAssignments.Any(entity => entity.State == "REVOKED" || entity.RevokedAt is not null))
            {
                return Deny("ASSIGNMENT_REVOKED");
            }

            if (allSubjectAssignments.Any(entity => entity.ValidUntil <= now || entity.State == "EXPIRED"))
            {
                return Deny("ASSIGNMENT_EXPIRED");
            }

            return Deny("NO_ACTIVE_ASSIGNMENT");
        }

        HashSet<Guid> roleRefIds = activeAssignments.Select(entity => entity.RoleRefId).ToHashSet();
        Dictionary<Guid, string> roleKeyById = await dbContext.Roles
            .AsNoTracking()
            .Where(entity => entity.ApplicationRefId == application.RowId)
            .ToDictionaryAsync(entity => entity.Id, entity => entity.RoleKey, cancellationToken);
        string[] roleKeys = roleRefIds
            .Where(roleKeyById.ContainsKey)
            .Select(id => roleKeyById[id])
            .ToArray();
        List<RolePermissionEntity> mappings = await dbContext.RolePermissions
            .AsNoTracking()
            .Where(entity => entity.ApplicationRefId == application.RowId
                && roleRefIds.Contains(entity.RoleRefId)
                && entity.PermissionRefId == permission.RowId
                && entity.State == "PUBLISHED")
            .ToListAsync(cancellationToken);

        if (mappings.Count == 0)
        {
            return Deny("PERMISSION_NOT_GRANTED", matchedRoles: roleKeys);
        }

        List<PolicyEntity> policies = await dbContext.Policies
            .AsNoTracking()
            .Where(entity => entity.ApplicationRefId == application.RowId
                && entity.PermissionRefId == permission.RowId
                && entity.State == "PUBLISHED")
            .ToListAsync(cancellationToken);

        if (policies.Count == 0)
        {
            // Case 1 — no policies published: the role grant alone authorizes the action (RBAC
            // baseline). Policies are an optional, additive guardrail layer on top of RBAC.
            return Allow(roleKeys, [permission.PermissionKey], []);
        }

        bool hasAllowPolicy = policies.Any(entity => entity.Effect == "ALLOW");

        Dictionary<Guid, Dictionary<string, string>> attributesByAssignment = await LoadAssignmentAttributesAsync(activeAssignments, cancellationToken);
        List<AssignmentEntity> evaluableAssignments = activeAssignments
            .Where(entity => roleRefIds.Contains(entity.RoleRefId))
            .ToList();
        var matchedPolicies = new List<MatchedPolicy>();
        bool missingContext = false;

        // Computed "system.*" attributes (evaluation clock) are exposed to conditions so policies
        // can reason about the current time without the caller supplying it. Built once per decision.
        IReadOnlyDictionary<string, string> systemContext = BuildSystemContext(now);

        // "reference.*" documents are shared, application-scoped lookup lists. Only queried when a
        // policy actually references them, so unaffected decisions incur no extra database round-trip.
        IReadOnlyDictionary<string, string> referenceData = await LoadReferenceDataIfNeededAsync(application, policies, cancellationToken);

        foreach (PolicyEntity policy in policies)
        {
            // Parse the policy's condition document once per decision rather than once per
            // assignment: the parsed structure is identical across assignments; only the
            // attribute/context values compared during evaluation differ.
            using JsonDocument conditionsDocument = JsonDocument.Parse(policy.Conditions);
            JsonElement root = conditionsDocument.RootElement;
            bool hasConditions = root.TryGetProperty("conditions", out JsonElement conditions)
                && conditions.ValueKind == JsonValueKind.Array
                && conditions.GetArrayLength() > 0;
            string match = root.TryGetProperty("match", out JsonElement matchElement) && matchElement.ValueKind == JsonValueKind.String
                ? matchElement.GetString() ?? "all"
                : "all";

            bool policyMatched = false;
            foreach (AssignmentEntity assignment in evaluableAssignments)
            {
                PolicyEvaluationResult result = hasConditions
                    ? EvaluateGroup(match, conditions, request.Context, attributesByAssignment.GetValueOrDefault(assignment.Id), systemContext, referenceData)
                    : new PolicyEvaluationResult(Matches: true, MissingContext: false);
                missingContext |= result.MissingContext;

                if (result.Matches)
                {
                    policyMatched = true;
                }
            }

            if (policyMatched)
            {
                matchedPolicies.Add(new MatchedPolicy(policy.PolicyKey, policy.Effect, policy.Priority, policy.Obligations));
            }
        }

        // Resolve which matched policy decides the outcome under the application's configured
        // combining algorithm (deny-overrides by default, preserving historical behavior).
        MatchedPolicy? deciding = SelectDecidingPolicy(application.PolicyCombiningAlgorithm, matchedPolicies);

        if (deciding is not null)
        {
            string[] decidingKeys = matchedPolicies
                .Where(entity => string.Equals(entity.Effect, deciding.Effect, StringComparison.OrdinalIgnoreCase))
                .Select(entity => entity.PolicyKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            IReadOnlyCollection<AuthorizeObligation> obligations = CollectObligations(matchedPolicies, deciding.Effect);

            return string.Equals(deciding.Effect, "DENY", StringComparison.OrdinalIgnoreCase)
                ? Deny("EXPLICIT_DENY", roleKeys, [permission.PermissionKey], decidingKeys, obligations)
                : Allow(roleKeys, [permission.PermissionKey], decidingKeys, obligations);
        }

        // Case 2b — no policy matched. If only DENY policies exist (and every guard could evaluate)
        // the RBAC baseline still authorizes. If any allow policy exists but none matched, or a
        // guard's context was missing, we fall through to a fail-closed deny.
        if (!hasAllowPolicy && !missingContext)
        {
            return Allow(roleKeys, [permission.PermissionKey], []);
        }

        return Deny(missingContext ? "MISSING_CONTEXT" : "DENY_BY_DEFAULT", roleKeys, [permission.PermissionKey]);
    }

    private async Task<ApplicationReference?> GetApplicationAsync(string applicationId, CancellationToken cancellationToken)
    {
        ApplicationEntity? application = await dbContext.Applications.FirstOrDefaultAsync(
            entity => entity.ApplicationId == applicationId && entity.Status == "ACTIVE",
            cancellationToken);
        if (application is null)
        {
            return null;
        }

        return new ApplicationReference(application.Id, application.ApplicationId, application.Status, application.PolicyCombiningAlgorithm);
    }

    private async Task<PermissionReference?> GetPermissionAsync(ApplicationReference application, string resourceType, string action, CancellationToken cancellationToken)
    {
        PermissionEntity? permission = await dbContext.Permissions.FirstOrDefaultAsync(
            entity => entity.ApplicationRefId == application.RowId
                && entity.Resource == resourceType
                && entity.Action == action
                && entity.Status == "ACTIVE",
            cancellationToken);
        if (permission is null)
        {
            return null;
        }

        return new PermissionReference(permission.Id, application.ApplicationId, permission.PermissionKey, permission.Resource, permission.Action, permission.Status);
    }

    private async Task<Dictionary<Guid, Dictionary<string, string>>> LoadAssignmentAttributesAsync(List<AssignmentEntity> assignments, CancellationToken cancellationToken)
    {
        Guid[] assignmentIds = assignments.Select(entity => entity.Id).ToArray();
        List<AssignmentAttributeEntity> attributes = await dbContext.AssignmentAttributes
            .Where(entity => assignmentIds.Contains(entity.AssignmentId))
            .ToListAsync(cancellationToken);

        return attributes
            .GroupBy(entity => entity.AssignmentId)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(entity => entity.Name, entity => entity.Value, StringComparer.OrdinalIgnoreCase));
    }

    // Recursively evaluates a group of nodes. A node is either a leaf ({attribute, operator, value})
    // or a nested group ({match, conditions:[...]}). "all" => AND, "any" => OR, "none" => NOT any.
    // Back-compatible: a legacy root with just {conditions:[...]} evaluates as an "all" (AND) group.
    private static PolicyEvaluationResult EvaluateGroup(
        string match,
        JsonElement conditions,
        IReadOnlyDictionary<string, object?> context,
        Dictionary<string, string>? assignmentAttributes,
        IReadOnlyDictionary<string, string> systemContext,
        IReadOnlyDictionary<string, string> referenceData)
    {
        bool any = string.Equals(match, PolicyOperators.MatchAny, StringComparison.OrdinalIgnoreCase);
        bool none = string.Equals(match, PolicyOperators.MatchNone, StringComparison.OrdinalIgnoreCase);
        bool anyMatched = false;
        bool allMatched = true;
        bool missingContext = false;

        foreach (JsonElement node in conditions.EnumerateArray())
        {
            PolicyEvaluationResult result;
            if (node.TryGetProperty("conditions", out JsonElement childConditions) && childConditions.ValueKind == JsonValueKind.Array)
            {
                string childMatch = node.TryGetProperty("match", out JsonElement childMatchElement) && childMatchElement.ValueKind == JsonValueKind.String
                    ? childMatchElement.GetString() ?? "all"
                    : "all";
                result = EvaluateGroup(childMatch, childConditions, context, assignmentAttributes, systemContext, referenceData);
            }
            else
            {
                result = EvaluateLeaf(node, context, assignmentAttributes, systemContext, referenceData);
            }

            missingContext |= result.MissingContext;
            if (result.Matches)
            {
                anyMatched = true;
            }
            else
            {
                allMatched = false;
            }
        }

        bool matches = none ? !anyMatched : (any ? anyMatched : allMatched);
        return new PolicyEvaluationResult(Matches: matches, MissingContext: matches ? false : missingContext);
    }

    private static PolicyEvaluationResult EvaluateLeaf(
        JsonElement condition,
        IReadOnlyDictionary<string, object?> context,
        Dictionary<string, string>? assignmentAttributes,
        IReadOnlyDictionary<string, string> systemContext,
        IReadOnlyDictionary<string, string> referenceData)
    {
        string attribute = condition.TryGetProperty("attribute", out JsonElement attributeElement)
            ? attributeElement.GetString() ?? string.Empty
            : string.Empty;
        string operatorName = condition.TryGetProperty("operator", out JsonElement operatorElement)
            ? operatorElement.GetString() ?? string.Empty
            : string.Empty;
        string expectedExpression = string.Empty;
        if (condition.TryGetProperty("value", out JsonElement valueElement))
        {
            expectedExpression = valueElement.ValueKind == JsonValueKind.String
                ? valueElement.GetString() ?? string.Empty
                : valueElement.GetRawText();
        }

        if (!TryResolveValue(attribute, context, assignmentAttributes, systemContext, referenceData, out string? actualValue)
            || !TryResolveExpectedValue(expectedExpression, context, assignmentAttributes, systemContext, referenceData, out string? expectedValue))
        {
            return new PolicyEvaluationResult(Matches: false, MissingContext: true);
        }

        return new PolicyEvaluationResult(Matches: Compare(actualValue!, expectedValue!, operatorName), MissingContext: false);
    }

    private static bool TryResolveExpectedValue(string expression, IReadOnlyDictionary<string, object?> context, Dictionary<string, string>? assignmentAttributes, IReadOnlyDictionary<string, string> systemContext, IReadOnlyDictionary<string, string> referenceData, out string? value)
    {
        if (expression.StartsWith("context.", StringComparison.OrdinalIgnoreCase)
            || expression.StartsWith("assignment.", StringComparison.OrdinalIgnoreCase)
            || expression.StartsWith("system.", StringComparison.OrdinalIgnoreCase)
            || expression.StartsWith("reference.", StringComparison.OrdinalIgnoreCase))
        {
            return TryResolveValue(expression, context, assignmentAttributes, systemContext, referenceData, out value);
        }

        value = expression;
        return true;
    }

    private static bool TryResolveValue(string expression, IReadOnlyDictionary<string, object?> context, Dictionary<string, string>? assignmentAttributes, IReadOnlyDictionary<string, string> systemContext, IReadOnlyDictionary<string, string> referenceData, out string? value)
    {
        if (expression.StartsWith("context.", StringComparison.OrdinalIgnoreCase))
        {
            string key = expression["context.".Length..];
            if (TryResolveContextPath(context, key, out value))
            {
                return value is not null;
            }
        }
        else if (expression.StartsWith("assignment.", StringComparison.OrdinalIgnoreCase))
        {
            string key = expression["assignment.".Length..];
            if (assignmentAttributes is not null && assignmentAttributes.TryGetValue(key, out string? assignmentValue))
            {
                value = assignmentValue.Trim('"');
                return true;
            }
        }
        else if (expression.StartsWith("system.", StringComparison.OrdinalIgnoreCase))
        {
            string key = expression["system.".Length..];
            if (systemContext.TryGetValue(key, out string? systemValue))
            {
                value = systemValue;
                return true;
            }
        }
        else if (expression.StartsWith("reference.", StringComparison.OrdinalIgnoreCase))
        {
            string key = expression["reference.".Length..];
            if (TryResolveReferencePath(referenceData, key, out value))
            {
                return value is not null;
            }
        }

        value = null;
        return false;
    }

    // Resolves a "reference.<key>" or "reference.<key>.<path>" expression against the application's
    // stored reference-data documents. A whole-key match returns the raw document text; otherwise
    // the remainder is walked into the document's JSON structure via the shared path traverser.
    private static bool TryResolveReferencePath(IReadOnlyDictionary<string, string> referenceData, string key, out string? value)
    {
        if (referenceData.TryGetValue(key, out string? whole))
        {
            value = whole;
            return true;
        }

        int dot = key.IndexOf('.');
        if (dot < 0)
        {
            value = null;
            return false;
        }

        string head = key[..dot];
        string rest = key[(dot + 1)..];
        if (!referenceData.TryGetValue(head, out string? document))
        {
            value = null;
            return false;
        }

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(document);
            return TryTraversePath(parsed.RootElement, rest, out value);
        }
        catch (JsonException)
        {
            value = null;
            return false;
        }
    }

    // Resolves a "context.<path>" reference. A flat key match wins first (keys may legitimately
    // contain dots); otherwise the dotted path is walked into nested dictionaries / JSON objects.
    private static bool TryResolveContextPath(IReadOnlyDictionary<string, object?> context, string key, out string? value)
    {
        if (context.TryGetValue(key, out object? direct) && direct is not null)
        {
            value = Convert.ToString(direct, CultureInfo.InvariantCulture);
            return true;
        }

        int dot = key.IndexOf('.');
        if (dot < 0)
        {
            value = null;
            return false;
        }

        string head = key[..dot];
        string rest = key[(dot + 1)..];
        if (!context.TryGetValue(head, out object? node) || node is null)
        {
            value = null;
            return false;
        }

        return TryTraversePath(node, rest, out value);
    }

    // Walks a dotted path into a nested JSON object or dictionary, returning the leaf as a string.
    private static bool TryTraversePath(object node, string path, out string? value)
    {
        object? current = node;
        foreach (string segment in path.Split('.'))
        {
            switch (current)
            {
                case JsonElement je when je.ValueKind == JsonValueKind.Object && je.TryGetProperty(segment, out JsonElement child):
                    current = child;
                    break;
                case IReadOnlyDictionary<string, object?> roDict when roDict.TryGetValue(segment, out object? next):
                    current = next;
                    break;
                case IDictionary<string, object?> dict when dict.TryGetValue(segment, out object? next):
                    current = next;
                    break;
                default:
                    value = null;
                    return false;
            }

            if (current is null)
            {
                value = null;
                return false;
            }
        }

        value = current is JsonElement leaf
            ? (leaf.ValueKind == JsonValueKind.String ? leaf.GetString() : leaf.GetRawText())
            : Convert.ToString(current, CultureInfo.InvariantCulture);
        return value is not null;
    }

    // Computed evaluation-clock attributes exposed to conditions as "system.*".
    private static Dictionary<string, string> BuildSystemContext(DateTimeOffset now) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["now"] = now.ToString("O", CultureInfo.InvariantCulture),
        ["date"] = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["time"] = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        ["hour"] = now.Hour.ToString(CultureInfo.InvariantCulture),
        ["dayOfWeek"] = now.DayOfWeek.ToString(),
        // ISO-8601 weekday number: Monday=1 … Sunday=7.
        ["dow"] = (((int)now.DayOfWeek + 6) % 7 + 1).ToString(CultureInfo.InvariantCulture),
    };

    private static bool Compare(string actualValue, string expectedValue, string operatorName)
    {
        return operatorName switch
        {
            "eq" => string.Equals(actualValue, expectedValue, StringComparison.OrdinalIgnoreCase),
            "neq" => !string.Equals(actualValue, expectedValue, StringComparison.OrdinalIgnoreCase),
            "gt" => TryCompareTyped(actualValue, expectedValue, out int gtResult) && gtResult > 0,
            "gte" => TryCompareTyped(actualValue, expectedValue, out int gteResult) && gteResult >= 0,
            "lt" => TryCompareTyped(actualValue, expectedValue, out int ltResult) && ltResult < 0,
            "lte" => TryCompareTyped(actualValue, expectedValue, out int lteResult) && lteResult <= 0,
            "before" => TryCompareDateTime(actualValue, expectedValue, out int beforeResult) && beforeResult < 0,
            "after" => TryCompareDateTime(actualValue, expectedValue, out int afterResult) && afterResult > 0,
            "between" => EvaluateBetween(actualValue, expectedValue),
            "in" => SplitValues(expectedValue).Contains(actualValue, StringComparer.OrdinalIgnoreCase),
            "notIn" => !SplitValues(expectedValue).Contains(actualValue, StringComparer.OrdinalIgnoreCase),
            "containsAny" => SplitValues(actualValue).Intersect(SplitValues(expectedValue), StringComparer.OrdinalIgnoreCase).Any(),
            "containsAll" => SplitValues(expectedValue).All(expected => SplitValues(actualValue).Contains(expected, StringComparer.OrdinalIgnoreCase)),
            "contains" => actualValue.Contains(expectedValue, StringComparison.OrdinalIgnoreCase),
            "notContains" => !actualValue.Contains(expectedValue, StringComparison.OrdinalIgnoreCase),
            "startsWith" => actualValue.StartsWith(expectedValue, StringComparison.OrdinalIgnoreCase),
            "endsWith" => actualValue.EndsWith(expectedValue, StringComparison.OrdinalIgnoreCase),
            "matches" => TryRegexMatch(actualValue, expectedValue),
            "notMatches" => !TryRegexMatch(actualValue, expectedValue),
            "exists" => !string.IsNullOrWhiteSpace(actualValue),
            "notExists" => string.IsNullOrWhiteSpace(actualValue),
            "isTrue" => IsTruthy(actualValue),
            "isFalse" => IsFalsy(actualValue),
            _ => false,
        };
    }

    // Typed comparison used by ordering operators: attempts decimal, then date/time. This is what
    // makes date and numeric gt/gte/lt/lte/between work. When neither side parses as a number or a
    // date the comparison fails (no match) rather than falling back to surprising lexical ordering,
    // preserving the fail-closed contract for non-comparable values.
    private static bool TryCompareTyped(string actual, string expected, out int result)
    {
        if (decimal.TryParse(actual, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal actualDecimal)
            && decimal.TryParse(expected, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal expectedDecimal))
        {
            result = actualDecimal.CompareTo(expectedDecimal);
            return true;
        }

        if (TryParseDateTime(actual, out DateTimeOffset actualDate) && TryParseDateTime(expected, out DateTimeOffset expectedDate))
        {
            result = actualDate.CompareTo(expectedDate);
            return true;
        }

        result = 0;
        return false;
    }

    // Strict date/time comparison used by before/after: fails (no match) when either side is
    // not a parseable date/time so these operators never fall back to lexical ordering.
    private static bool TryCompareDateTime(string actual, string expected, out int result)
    {
        if (TryParseDateTime(actual, out DateTimeOffset actualDate) && TryParseDateTime(expected, out DateTimeOffset expectedDate))
        {
            result = actualDate.CompareTo(expectedDate);
            return true;
        }

        result = 0;
        return false;
    }

    private static bool TryParseDateTime(string value, out DateTimeOffset result) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result);

    // Inclusive range check "min,max" for numbers or dates via the typed comparer.
    private static bool EvaluateBetween(string actual, string expected)
    {
        string[] bounds = expected.Split(',', 2, StringSplitOptions.TrimEntries);
        if (bounds.Length != 2)
        {
            return false;
        }

        return TryCompareTyped(actual, bounds[0], out int lower) && lower >= 0
            && TryCompareTyped(actual, bounds[1], out int upper) && upper <= 0;
    }

    // Splits a value into list members, accepting either a JSON array (e.g. ["a","b"]) or a
    // comma-separated string. Used by in/notIn and the collection operators.
    private static string[] SplitValues(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return document.RootElement.EnumerateArray()
                        .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText())
                        .ToArray();
                }
            }
            catch (JsonException)
            {
                // Fall through to comma splitting for malformed bracketed input.
            }
        }

        return trimmed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    // Regex match hardened against ReDoS: linear-time NonBacktracking engine plus a hard timeout.
    // Invalid or unsupported patterns evaluate to no-match rather than throwing.
    private static bool TryRegexMatch(string input, string pattern)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool IsTruthy(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase);

    private static bool IsFalsy(string value) =>
        value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0" || value.Equals("no", StringComparison.OrdinalIgnoreCase);

    private static AuthorizeDecision Deny(
        string denyReason,
        IEnumerable<string>? matchedRoles = null,
        IEnumerable<string>? matchedPermissions = null,
        IEnumerable<string>? matchedPolicies = null,
        IReadOnlyCollection<AuthorizeObligation>? obligations = null)
    {
        return new AuthorizeDecision(
            CreateDecisionId(),
            Allowed: false,
            denyReason,
            (matchedRoles ?? []).ToArray(),
            (matchedPermissions ?? []).ToArray(),
            (matchedPolicies ?? []).ToArray(),
            obligations ?? []);
    }

    private static AuthorizeDecision Allow(
        IEnumerable<string> matchedRoles,
        IEnumerable<string> matchedPermissions,
        IEnumerable<string> matchedPolicies,
        IReadOnlyCollection<AuthorizeObligation>? obligations = null)
    {
        return new AuthorizeDecision(
            CreateDecisionId(),
            Allowed: true,
            DenyReason: null,
            matchedRoles.ToArray(),
            matchedPermissions.ToArray(),
            matchedPolicies.ToArray(),
            obligations ?? []);
    }

    // Applies the application's configured combining algorithm to the set of matched policies and
    // returns the single policy whose effect decides the outcome (or null when nothing matched).
    // Ties are broken by descending priority then ordinal policy key so results are deterministic.
    private static MatchedPolicy? SelectDecidingPolicy(string? algorithm, List<MatchedPolicy> matched)
    {
        if (matched.Count == 0)
        {
            return null;
        }

        return (algorithm ?? "deny-overrides") switch
        {
            "allow-overrides" => FirstByPriority(matched.Where(policy => IsEffect(policy, "ALLOW")))
                ?? FirstByPriority(matched.Where(policy => IsEffect(policy, "DENY"))),
            "first-applicable" => FirstByPriority(matched),
            _ => FirstByPriority(matched.Where(policy => IsEffect(policy, "DENY")))
                ?? FirstByPriority(matched.Where(policy => IsEffect(policy, "ALLOW"))),
        };
    }

    private static bool IsEffect(MatchedPolicy policy, string effect) =>
        string.Equals(policy.Effect, effect, StringComparison.OrdinalIgnoreCase);

    private static MatchedPolicy? FirstByPriority(IEnumerable<MatchedPolicy> policies) =>
        policies
            .OrderByDescending(policy => policy.Priority)
            .ThenBy(policy => policy.PolicyKey, StringComparer.Ordinal)
            .FirstOrDefault();

    // Gathers obligations from every matched policy whose effect equals the final decision effect,
    // de-duplicated by id (first occurrence wins), ordered by descending priority then policy key.
    private static IReadOnlyCollection<AuthorizeObligation> CollectObligations(IEnumerable<MatchedPolicy> matched, string effect)
    {
        var result = new List<AuthorizeObligation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<MatchedPolicy> ordered = matched
            .Where(policy => IsEffect(policy, effect))
            .OrderByDescending(policy => policy.Priority)
            .ThenBy(policy => policy.PolicyKey, StringComparer.Ordinal);

        foreach (MatchedPolicy policy in ordered)
        {
            foreach (AuthorizeObligation obligation in ParseObligations(policy.ObligationsJson))
            {
                if (seen.Add(obligation.Id))
                {
                    result.Add(obligation);
                }
            }
        }

        return result;
    }

    // Parses a policy's stored obligations JSON. Accepts a JSON array of either bare id strings
    // (e.g. ["require_mfa"]) or objects ({"id":"mask_ssn","value":"last4"}). Malformed input yields
    // no obligations rather than throwing, so a bad document never blocks a decision.
    private static List<AuthorizeObligation> ParseObligations(string? json)
    {
        var result = new List<AuthorizeObligation>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    string? id = element.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        result.Add(new AuthorizeObligation(id, null));
                    }
                }
                else if (element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty("id", out JsonElement idElement)
                    && idElement.ValueKind == JsonValueKind.String)
                {
                    string? id = idElement.GetString();
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    string? value = null;
                    if (element.TryGetProperty("value", out JsonElement valueElement) && valueElement.ValueKind != JsonValueKind.Null)
                    {
                        value = valueElement.ValueKind == JsonValueKind.String
                            ? valueElement.GetString()
                            : valueElement.GetRawText();
                    }

                    result.Add(new AuthorizeObligation(id, value));
                }
            }
        }
        catch (JsonException)
        {
            // A malformed obligations document contributes no obligations.
        }

        return result;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyReferenceData =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Loads the application's ACTIVE reference-data documents keyed by their reference key, but only
    // when at least one published policy actually mentions "reference." — decisions that never use
    // reference data pay no query cost.
    private async Task<IReadOnlyDictionary<string, string>> LoadReferenceDataIfNeededAsync(
        ApplicationReference application,
        List<PolicyEntity> policies,
        CancellationToken cancellationToken)
    {
        bool referenced = policies.Any(policy =>
            policy.Conditions.Contains("reference.", StringComparison.OrdinalIgnoreCase));
        if (!referenced)
        {
            return EmptyReferenceData;
        }

        List<ReferenceDataEntity> rows = await dbContext.ReferenceData
            .AsNoTracking()
            .Where(entity => entity.ApplicationRefId == application.RowId && entity.Status == "ACTIVE")
            .ToListAsync(cancellationToken);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ReferenceDataEntity row in rows)
        {
            map[row.Key] = row.Value;
        }

        return map;
    }

    private static string CreateDecisionId()
    {
        return Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    }

    private sealed record PolicyEvaluationResult(bool Matches, bool MissingContext);

    private sealed record MatchedPolicy(string PolicyKey, string Effect, int Priority, string ObligationsJson);
}

public sealed record ApplicationReference(Guid RowId, string ApplicationId, string Status, string PolicyCombiningAlgorithm);

public sealed record PermissionReference(Guid RowId, string ApplicationId, string PermissionKey, string ResourceType, string Action, string Status);
