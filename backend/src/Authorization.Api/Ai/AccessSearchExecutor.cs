using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Authorization.Ai;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Ai;
/// <summary>
/// F8 — executes a <b>validated, closed</b> access-search spec against the structured
/// access model with parameterized EF queries. The AI layer only ever produces a spec in
/// this closed schema (entity + filters over a fixed field allow-list); it never emits SQL.
/// Any spec that references an unknown entity, field, or operator is rejected here, so an
/// invented field can never reach the database. Read-only: no query mutates state.
/// </summary>
public sealed class AccessSearchExecutor
{
    private const int PerEntityLimit = 100;

    // The closed schema: the only entities and fields a spec may reference. Anything else → invalid.
    private static readonly IReadOnlyDictionary<string, string[]> Schema =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["role"] = ["roleKey", "name", "riskLevel", "privileged", "status", "permission", "resource", "action"],
            ["permission"] = ["permissionKey", "resource", "action", "riskLevel", "status"],
            ["policy"] = ["policyKey", "effect", "state", "permission", "resource", "action"],
            ["assignment"] = ["subject", "role", "resourceType", "resourceId", "state", "source", "validUntil"],
            ["subject"] = ["subject", "role", "permission", "resource", "action", "resourceId"],
        };

    // The closed operator set. Beyond eq/contains, the spec now supports negation, set membership and
    // ordered comparisons (numeric/date). Every operator is still executed via parameterized EF over
    // an allow-listed field — the model never authors SQL.
    private static readonly HashSet<string> Operators =
        new(StringComparer.OrdinalIgnoreCase) { "eq", "neq", "contains", "in", "gt", "gte", "lt", "lte" };

    // Allow-listed low-cardinality fields a built-in entity may be grouped by (a breakdown/histogram).
    // Date/high-cardinality fields and registry entities are intentionally excluded for now.
    private static readonly IReadOnlyDictionary<string, string[]> GroupableFields =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["role"] = ["riskLevel", "status", "privileged"],
            ["permission"] = ["riskLevel", "status"],
            ["policy"] = ["effect", "state"],
            ["assignment"] = ["state"],
        };

    // Resolves the caller's requested row cap to a bounded value: a positive limit is honored up to
    // the hard PerEntityLimit ceiling; anything else falls back to PerEntityLimit. Group results are
    // already ordered by count (desc), so taking the first N yields the top N.
    private static int EffectiveLimit(int? limit) =>
        limit is int n && n > 0 ? Math.Min(n, PerEntityLimit) : PerEntityLimit;

    // Additional governance entities (beyond the five hand-written ones) answerable via a generic,
    // closed, reflection-derived descriptor. Stateless, so a single shared instance is safe.
    private static readonly AccessSearchRegistry Registry = new();

    private readonly AuthorizationDbContext dbContext;

    public AccessSearchExecutor(AuthorizationDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    /// <summary>The closed schema, exposed so the API can ground the model in the allowed vocabulary.</summary>
    public static IReadOnlyDictionary<string, string[]> EntitySchema => Schema;

    // Parent → child relationships the search can traverse (relationship counts + include). Stateless.
    private static readonly AccessRelationshipRegistry Relationships = new();

    // Cap on children returned per parent when including related entities.
    private const int ChildPerParentLimit = 50;

    // Short, plain-language description of each hand-written entity so the planner knows what it models.
    private static readonly IReadOnlyDictionary<string, string> EntityDescriptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["role"] = "A named bundle of permissions that can be assigned to subjects.",
            ["permission"] = "A single capability = resource + action, identified by a permission key.",
            ["policy"] = "An ALLOW/DENY rule attached to a permission, optionally conditional.",
            ["assignment"] = "A grant linking a subject to a role; carries lifecycle state and an optional expiry.",
            ["subject"] = "A person/user derived from assignments; use this entity for \"who can …\" questions.",
        };

    /// <summary>
    /// A compact map of the access model so the planner understands how entities relate, which drives
    /// correct entity selection (e.g. "who can …" must traverse subject → assignment → role → permission).
    /// </summary>
    public const string HierarchyOverview =
        "Access model hierarchy: a Tenant owns Applications, and each Application owns its Roles, Permissions, Policies and Assignments. "
        + "A Role grants Permissions through published role-permission grants. "
        + "A Permission = resource + action and can have ALLOW/DENY Policies attached. "
        + "Subjects (people/users) receive Roles through Assignments; an Assignment has a lifecycle state "
        + "(ACTIVE, EXPIRED, REVOKED) and an optional validUntil expiry timestamp. "
        + "Answer \"who can do X\" with the subject entity (subject ← assignment → role → permission); "
        + "\"which roles grant X\" with the role entity; questions about grants/expiry with the assignment entity. "
        + "A subject's effective permissions and the applications they can reach follow subject → permissions and "
        + "subject → applications; the policies governing a role follow role → policies (and policy → roles for the reverse). "
        + "\"Who/which subjects have access to application X\" is answered from the application side, not subject: "
        + "entity application, filter applicationId eq X, include subjects (subject has no filterable application field "
        + "and does not support presentRelationship). "
        + "Both tenant and application have two distinct identifying fields: a URL-safe slug id (tenant.tenantId, "
        + "application.applicationId — lowercase, hyphenated, no spaces) and a separate display name (tenant.name, "
        + "application.name — a spaced, capitalized human-readable phrase). These are NOT interchangeable text, so filter "
        + "whichever field's real form (hyphenated slug vs spaced display phrase) matches the value as the question stated "
        + "it, and never assume the display name equals the slug with dashes swapped for spaces or vice versa. "
        + "For per-tenant roll-ups (e.g. \"how many roles/permissions/policies/applications/assignments per tenant\", "
        + "or \"which tenant has the most roles\"), choose the tenant entity and set groupByRelationship to the matching "
        + "relationship; for per-application roll-ups, choose the application entity and groupByRelationship its relationship. "
        + "\"Which applications belong to tenant X\" / \"show all applications for tenant X\" is entity tenant, filter name "
        + "eq X, include applications — application has no tenant name field, only an internal tenantRefId, which must "
        + "never be used as a filter value. Conversely, \"which tenant owns application X\" / \"who owns application X\" "
        + "is entity application, filter applicationId eq X, include tenant — tenant has no application-identifying "
        + "field, so it cannot be filtered directly by an application id or name.";

    // Per-field metadata (type + allowed value domain + description) keyed by field name. A few field
    // names (state) mean different things per entity, so those are resolved with an override below.
    private static readonly IReadOnlyDictionary<string, AccessSearchFieldSchema> FieldMeta =
        new Dictionary<string, AccessSearchFieldSchema>(StringComparer.OrdinalIgnoreCase)
        {
            ["roleKey"] = Field("roleKey", "ref", null, "Stable role key. Use values from vocabulary.roleKeys."),
            ["permissionKey"] = Field("permissionKey", "ref", null, "Stable permission key. Use values from vocabulary.permissionKeys."),
            ["policyKey"] = Field("policyKey", "string", null, "Stable policy key."),
            ["name"] = Field("name", "string", null, "Human-readable name."),
            ["riskLevel"] = Field("riskLevel", "enum", ["LOW", "MEDIUM", "HIGH", "CRITICAL"], "Risk classification."),
            ["privileged"] = Field("privileged", "bool", ["true", "false"], "Whether the role is privileged (high-risk)."),
            ["status"] = Field("status", "enum", ["ACTIVE", "DISABLED", "DEPRECATED", "ARCHIVED"], "Lifecycle status; \"inactive\" means status is not ACTIVE."),
            ["effect"] = Field("effect", "enum", ["ALLOW", "DENY"], "Whether the policy allows or denies."),
            ["permission"] = Field("permission", "ref", null, "Related permission key. Use values from vocabulary.permissionKeys."),
            ["resource"] = Field("resource", "ref", null, "Resource segment of a permission. Use values from vocabulary.resources."),
            ["action"] = Field("action", "ref", null, "Action segment of a permission. Use values from vocabulary.actions."),
            ["role"] = Field("role", "ref", null, "Related role key. Use values from vocabulary.roleKeys."),
            ["subject"] = Field("subject", "string", null, "Subject/user identifier or email."),
            ["resourceType"] = Field("resourceType", "string", null, "Type of the scoped resource, when the grant is resource-scoped."),
            ["resourceId"] = Field("resourceId", "ref", null, "Scoped resource identifier. Use values from vocabulary.resourceIds."),
            ["source"] = Field("source", "string", null, "How the assignment was created (e.g. MANUAL)."),
            ["validUntil"] = Field("validUntil", "date", null, "Assignment expiry timestamp. Use the literal \"now\" for the current time; state EXPIRED is equivalent to validUntil < now."),
        };

    // Resolves field metadata, applying the per-entity meaning of fields whose name is ambiguous across
    // entities ("state", "outcome").
    private static AccessSearchFieldSchema FieldSchemaFor(string entity, string field)
    {
        if (string.Equals(field, "state", StringComparison.OrdinalIgnoreCase))
        {
            // Policy and role-permission grants share the same publication lifecycle
            // (ck_policies_state / ck_role_permissions_state); assignment has its own, unrelated
            // ACTIVE/EXPIRED/REVOKED lifecycle.
            return string.Equals(entity, "policy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity, "rolePermission", StringComparison.OrdinalIgnoreCase)
                ? Field("state", "enum", ["DRAFT", "REVIEW", "APPROVED", "PUBLISHED"], "Publication state.")
                : Field("state", "enum", ["ACTIVE", "EXPIRED", "REVOKED"], "Assignment lifecycle state.");
        }

        if (string.Equals(field, "outcome", StringComparison.OrdinalIgnoreCase))
        {
            // aiInvocation and aiPromptLog both have an "outcome" field but with different, non-
            // overlapping domains — ground each explicitly so the model never guesses a value from
            // the other entity (e.g. "ValidationFailed" only exists on aiPromptLog).
            return string.Equals(entity, "aiPromptLog", StringComparison.OrdinalIgnoreCase)
                ? Field("outcome", "enum", ["Succeeded", "Unmapped", "ValidationFailed", "InvalidInput", "Timeout", "ModelError"], "How the prompt request concluded.")
                : Field("outcome", "enum", ["Success", "Timeout", "Error"], "How the AI invocation concluded.");
        }

        return FieldMeta.TryGetValue(field, out AccessSearchFieldSchema? meta)
            ? meta
            : Field(field, "string", null, null);
    }

    // Builds a field descriptor and attaches the operators that make sense for its type.
    private static AccessSearchFieldSchema Field(string name, string type, string[]? values, string? description)
    {
        string[] operators = type switch
        {
            "date" or "number" => ["eq", "neq", "gt", "gte", "lt", "lte"],
            "bool" or "enum" => ["eq", "neq", "in"],
            _ => ["eq", "neq", "contains", "in"],
        };
        return new AccessSearchFieldSchema(name, type, values, description, operators);
    }

    /// <summary>
    /// The closed schema shaped for the AI planner: the five hand-written entities plus every generic
    /// registry entity, each annotated with the relationships it can be grouped/expanded by, plus a
    /// per-field descriptor (type, allowed values, description) so the model can plan over the whole
    /// registered governance model without guessing operators or value domains.
    /// </summary>
    public static IReadOnlyList<AccessSearchEntitySchema> BuildPlannerSchema() =>
        Schema.Select(entry => new AccessSearchEntitySchema(
                entry.Key,
                entry.Value,
                Relationships.RelationshipsFor(entry.Key),
                EntityDescriptions.GetValueOrDefault(entry.Key),
                entry.Value.Select(f => FieldSchemaFor(entry.Key, f)).ToList()))
            .Concat(Registry.Schema.Select(s => new AccessSearchEntitySchema(
                s.Entity,
                s.Fields,
                Relationships.RelationshipsFor(s.Entity),
                null,
                s.Fields.Select(f => FieldSchemaFor(s.Entity, f)).ToList())))
            .ToList();


    /// <summary>
    /// Load the real, grounded vocabulary for the given applications so the model only ever produces
    /// values that actually exist. Distinct, capped, and read-only.
    /// </summary>
    public async Task<AccessSearchVocabulary> BuildVocabularyAsync(
        IReadOnlyCollection<Guid> applicationRefIds,
        CancellationToken cancellationToken)
    {
        if (applicationRefIds.Count == 0)
        {
            return new AccessSearchVocabulary([], [], [], [], []);
        }

        List<string> permissionKeys = await dbContext.Permissions.AsNoTracking()
            .Where(p => applicationRefIds.Contains(p.ApplicationRefId))
            .Select(p => p.PermissionKey)
            .Distinct().Take(500).ToListAsync(cancellationToken);

        List<string> resources = await dbContext.Permissions.AsNoTracking()
            .Where(p => applicationRefIds.Contains(p.ApplicationRefId))
            .Select(p => p.Resource)
            .Distinct().Take(500).ToListAsync(cancellationToken);

        List<string> actions = await dbContext.Permissions.AsNoTracking()
            .Where(p => applicationRefIds.Contains(p.ApplicationRefId))
            .Select(p => p.Action)
            .Distinct().Take(500).ToListAsync(cancellationToken);

        List<string> roleKeys = await dbContext.Roles.AsNoTracking()
            .Where(r => applicationRefIds.Contains(r.ApplicationRefId))
            .Select(r => r.RoleKey)
            .Distinct().Take(500).ToListAsync(cancellationToken);

        List<string> resourceIds = await dbContext.Assignments.AsNoTracking()
            .Where(a => applicationRefIds.Contains(a.ApplicationRefId) && a.ResourceId != null)
            .Select(a => a.ResourceId!)
            .Distinct().Take(500).ToListAsync(cancellationToken);

        return new AccessSearchVocabulary(permissionKeys, resources, actions, roleKeys, resourceIds);
    }

    // Caps for the relevance-scoped grounding sample. Kept small so the request stays well within the
    // model provider's payload/token limits even as the catalog grows to tens of thousands of keys.
    private const int RelevantSampleLimit = 40;
    private const int BaselineSampleLimit = 20;

    // Words that carry no grounding signal; dropped before matching so tokens are real domain terms.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "how", "many", "much", "the", "all", "for", "are", "which", "what", "show", "list", "who",
        "can", "does", "do", "did", "have", "has", "had", "with", "and", "that", "this", "from",
        "into", "per", "each", "of", "in", "on", "to", "is", "was", "were", "be", "been", "by",
        "or", "an", "any", "not", "no", "count", "number", "total", "give", "get", "find", "tell",
        "me", "us", "role", "roles", "permission", "permissions", "policy", "policies", "assignment",
        "assignments", "subject", "subjects", "user", "users", "people", "access",
    };

    /// <summary>
    /// Loads a <b>relevance-scoped</b> grounding vocabulary for the access-search planner: only values
    /// whose text overlaps the question's keywords, with a small baseline sample when nothing matches.
    /// This keeps the AI request bounded regardless of catalog size (avoids provider 413 / token
    /// limits) while surfacing the values the question is actually about. It is intentionally separate
    /// from <see cref="BuildVocabularyAsync"/>, which must stay complete for validation callers.
    /// Value correctness never depends on this sample — the executor still resolves and validates every
    /// filter value against the live data.
    /// </summary>
    public async Task<AccessSearchVocabulary> BuildRelevantVocabularyAsync(
        IReadOnlyCollection<Guid> applicationRefIds,
        string? question,
        CancellationToken cancellationToken)
    {
        if (applicationRefIds.Count == 0)
        {
            return new AccessSearchVocabulary([], [], [], [], []);
        }

        string[] tokens = ExtractKeywords(question);

        List<string> permissionKeys = await SampleAsync(
            dbContext.Permissions.AsNoTracking().Where(p => applicationRefIds.Contains(p.ApplicationRefId)).Select(p => p.PermissionKey),
            tokens, cancellationToken);

        List<string> resources = await SampleAsync(
            dbContext.Permissions.AsNoTracking().Where(p => applicationRefIds.Contains(p.ApplicationRefId)).Select(p => p.Resource),
            tokens, cancellationToken);

        List<string> actions = await SampleAsync(
            dbContext.Permissions.AsNoTracking().Where(p => applicationRefIds.Contains(p.ApplicationRefId)).Select(p => p.Action),
            tokens, cancellationToken);

        List<string> roleKeys = await SampleAsync(
            dbContext.Roles.AsNoTracking().Where(r => applicationRefIds.Contains(r.ApplicationRefId)).Select(r => r.RoleKey),
            tokens, cancellationToken);

        List<string> resourceIds = await SampleAsync(
            dbContext.Assignments.AsNoTracking().Where(a => applicationRefIds.Contains(a.ApplicationRefId) && a.ResourceId != null).Select(a => a.ResourceId!),
            tokens, cancellationToken);

        return new AccessSearchVocabulary(permissionKeys, resources, actions, roleKeys, resourceIds);
    }

    // Selects question-relevant distinct values (falling back to a small baseline sample so the model
    // always sees the naming pattern). The OR-of-Contains predicate is expression-built so it translates
    // on both Npgsql (runtime) and the EF in-memory provider (tests).
    private static async Task<List<string>> SampleAsync(
        IQueryable<string> values,
        string[] tokens,
        CancellationToken cancellationToken)
    {
        IQueryable<string> distinct = values.Distinct();

        if (tokens.Length > 0)
        {
            List<string> relevant = await distinct
                .Where(ContainsAnyToken(tokens))
                .Take(RelevantSampleLimit)
                .ToListAsync(cancellationToken);

            if (relevant.Count > 0)
            {
                return relevant;
            }
        }

        return await distinct.Take(BaselineSampleLimit).ToListAsync(cancellationToken);
    }

    // Builds v => v.ToLower().Contains(t0) || v.ToLower().Contains(t1) || ... — a translatable OR chain.
    private static Expression<Func<string, bool>> ContainsAnyToken(string[] tokens)
    {
        ParameterExpression param = Expression.Parameter(typeof(string), "v");
        MethodInfo toLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
        MethodInfo contains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
        Expression lowered = Expression.Call(param, toLower);

        Expression? body = null;
        foreach (string token in tokens)
        {
            Expression call = Expression.Call(lowered, contains, Expression.Constant(token));
            body = body is null ? call : Expression.OrElse(body, call);
        }

        return Expression.Lambda<Func<string, bool>>(body ?? Expression.Constant(true), param);
    }

    // Extracts lower-cased, de-duplicated domain keywords from the question, dropping stop words and
    // very short tokens, and adding a naive singular form so "invoices" also matches "invoice".
    private static string[] ExtractKeywords(string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return [];
        }

        var tokens = new List<string>();
        foreach (string raw in question.ToLowerInvariant().Split(
            [' ', '\t', '\n', '\r', ',', '.', ';', ':', '?', '!', '"', '\'', '(', ')', '[', ']', '{', '}', '/', '\\', '=', '<', '>'],
            StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim();
            if (token.Length < 3 || StopWords.Contains(token) || tokens.Contains(token))
            {
                continue;
            }

            tokens.Add(token);
            if (token.Length > 4 && token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal))
            {
                string singular = token[..^1];
                if (!tokens.Contains(singular))
                {
                    tokens.Add(singular);
                }
            }

            if (tokens.Count >= 12)
            {
                break;
            }
        }

        return [.. tokens];
    }

    /// <summary>
    /// Validate the spec against the closed schema and, if valid, execute it against a single
    /// application. Returns <see cref="AccessSearchOutcome.IsValid"/> = false (with a message) for any
    /// unknown entity/field/operator so the caller can respond 422.
    /// </summary>
    public async Task<AccessSearchOutcome> ExecuteAsync(
        Guid applicationRefId,
        string applicationId,
        AccessSearchSpec spec,
        CancellationToken cancellationToken)
    {
        string? validationError = Validate(spec);
        if (validationError is not null)
        {
            return new AccessSearchOutcome(false, validationError, spec.Entity, []);
        }

        // Scope is the single resolved application, matching how the platform controller iterates per
        // accessible app.
        var scope = new AccessScope([applicationRefId], [applicationId]);

        // Every row returned by this call belongs to the one scoped application, so resolve its
        // display name and owning tenant once and stamp them onto the rows. This lets the UI show
        // "which application / which tenant" in a readable way instead of a bare, cryptic id.
        (string appName, string tenantName) = await ResolveApplicationContextAsync(applicationRefId, cancellationToken);

        // Relationship include (tree): parent rows, each with their related children attached.
        if (!string.IsNullOrWhiteSpace(spec.Include))
        {
            List<AccessSearchRow> parents = await QueryEntityRowsAsync(applicationRefId, applicationId, scope, spec, PerEntityLimit, cancellationToken);
            List<AccessSearchRow> withChildren = await AttachChildrenAsync(scope, spec.Entity, spec.Include, parents, cancellationToken);

            // Bounded second level (C2): expand each attached child's own relationship one more hop,
            // e.g. role → permissions → policies. Validated above, so the child entity/relationship exist.
            string? childEntityForGrandchild = null;
            if (!string.IsNullOrWhiteSpace(spec.IncludeChild))
            {
                childEntityForGrandchild = Relationships.TargetEntityFor(spec.Entity, spec.Include);
                if (childEntityForGrandchild is not null)
                {
                    withChildren = await AttachGrandchildrenAsync(scope, childEntityForGrandchild, spec.IncludeChild, withChildren, cancellationToken);
                }
            }

            // Bounded third level: expand each attached grandchild's own relationship one more hop,
            // e.g. tenant → roles → permissions → policies. Validated above, so the grandchild
            // entity/relationship exist.
            if (!string.IsNullOrWhiteSpace(spec.IncludeGrandchild) && childEntityForGrandchild is not null)
            {
                string? grandchildEntity = Relationships.TargetEntityFor(childEntityForGrandchild, spec.IncludeChild!);
                if (grandchildEntity is not null)
                {
                    withChildren = await AttachGreatGrandchildrenAsync(scope, grandchildEntity, spec.IncludeGrandchild, withChildren, cancellationToken);
                }
            }

            return new AccessSearchOutcome(true, null, spec.Entity, StampApplicationContext(withChildren, applicationId, appName, tenantName), AccessSearchModes.Tree);
        }

        // Relationship presence / compound absence (C3): rows that HAVE presentRelationship and
        // (optionally) have NONE of absentRelationship, e.g. "roles that grant permissions but have no
        // assignments". Records mode; honors limit. Placed before the absence-only branch so a request
        // carrying both relationships is handled here.
        if (!string.IsNullOrWhiteSpace(spec.PresentRelationship))
        {
            List<AccessSearchRow> matches = await QueryRelationshipPresenceAbsenceAsync(
                applicationRefId, applicationId, scope, spec.Entity, spec.PresentRelationship, spec.AbsentRelationship, cancellationToken);
            matches = matches.Take(EffectiveLimit(spec.Limit)).ToList();
            return new AccessSearchOutcome(true, null, spec.Entity, StampApplicationContext(matches, applicationId, appName, tenantName), AccessSearchModes.Records);
        }

        // Relationship absence ("has none"): base-entity rows that have zero of the named relationship,
        // e.g. "roles with no assignments", "unused permissions". Returned as flat records so the UI can
        // list and deep-link into the orphaned entities.
        if (!string.IsNullOrWhiteSpace(spec.AbsentRelationship))
        {
            List<AccessSearchRow> orphans = await QueryRelationshipAbsenceAsync(
                applicationRefId, applicationId, scope, spec.Entity, spec.AbsentRelationship, cancellationToken);
            orphans = orphans.Take(EffectiveLimit(spec.Limit)).ToList();
            return new AccessSearchOutcome(true, null, spec.Entity, StampApplicationContext(orphans, applicationId, appName, tenantName), AccessSearchModes.Records);
        }

        // Relationship counts (group): one row per parent with its child count.
        if (!string.IsNullOrWhiteSpace(spec.GroupByRelationship))
        {
            List<AccessSearchRow> groupRows = await QueryRelationshipCountsAsync(
                applicationRefId, applicationId, scope, spec.Entity, spec.GroupByRelationship, cancellationToken);
            groupRows = groupRows.Take(EffectiveLimit(spec.Limit)).ToList();
            return new AccessSearchOutcome(true, null, spec.Entity, StampApplicationContext(groupRows, applicationId, appName, tenantName), AccessSearchModes.Group);
        }

        // Field group (breakdown): one row per distinct value of an allow-listed low-cardinality field,
        // with its count. These are aggregate buckets, not app-scoped rows, so they are not stamped with
        // a single application's context.
        if (!string.IsNullOrWhiteSpace(spec.GroupByField))
        {
            List<AccessSearchRow> fieldGroups = !string.IsNullOrWhiteSpace(spec.GroupBySecondaryField)
                ? await QueryFieldGroupNestedAsync(scope, spec.Entity, spec.GroupByField, spec.GroupBySecondaryField, cancellationToken)
                : await QueryFieldGroupAsync(scope, spec.Entity, spec.GroupByField, cancellationToken);
            fieldGroups = fieldGroups.Take(EffectiveLimit(spec.Limit)).ToList();
            return new AccessSearchOutcome(true, null, spec.Entity, fieldGroups, AccessSearchModes.Group);
        }

        // Scalar count. Also attach a bounded sample of the matching records (as children of the
        // count row) so the number is auditable — the user can see and deep-link into the actual
        // rows behind it, not just a bare total. The count stays authoritative; the sample is capped.
        if (spec.Aggregate is not null && spec.Aggregate.Equals("count", StringComparison.OrdinalIgnoreCase))
        {
            List<AccessSearchRow> sample = await QueryEntityRowsAsync(applicationRefId, applicationId, scope, spec, PerEntityLimit, cancellationToken);
            int total = await CountEntityAsync(applicationRefId, applicationId, scope, spec, cancellationToken);
            AccessSearchRow countRow = CountRow(applicationId, spec.Entity, total)
                with { Children = StampApplicationContext(sample, applicationId, appName, tenantName) };
            return new AccessSearchOutcome(true, null, spec.Entity, [countRow], AccessSearchModes.Count);
        }

        // Flat records. Built-in entity queries cap internally at PerEntityLimit, so apply the
        // caller's limit as a post-cap to honor it for both built-in and registry entities.
        List<AccessSearchRow> rows = await QueryEntityRowsAsync(applicationRefId, applicationId, scope, spec, PerEntityLimit, cancellationToken);
        rows = rows.Take(EffectiveLimit(spec.Limit)).ToList();
        return new AccessSearchOutcome(true, null, spec.Entity, StampApplicationContext(rows, applicationId, appName, tenantName), AccessSearchModes.Records);
    }

    // Resolves the display name of an application and the name of its owning tenant, so result rows
    // can present readable "application / tenant" context instead of raw ids. Returns empty strings
    // when the application cannot be found (e.g. it was just deleted), so stamping is a safe no-op.
    private async Task<(string ApplicationName, string TenantName)> ResolveApplicationContextAsync(
        Guid applicationRefId, CancellationToken cancellationToken)
    {
        var context = await dbContext.Applications.AsNoTracking()
            .Where(a => a.Id == applicationRefId)
            .Select(a => new
            {
                a.Name,
                TenantName = dbContext.Tenants.Where(t => t.Id == a.TenantRefId).Select(t => t.Name).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        return context is null
            ? (string.Empty, string.Empty)
            : (context.Name ?? string.Empty, context.TenantName ?? string.Empty);
    }

    // Stamps the scoped application id, application name and tenant name onto every row (recursing
    // into children) so the UI can show readable ownership. Rows that already carry a value keep it,
    // and rows that are themselves an application or tenant are left unlabelled (they are the context,
    // not owned by one). Applied per call because every row here belongs to the one scoped app.
    private static List<AccessSearchRow> StampApplicationContext(
        IReadOnlyList<AccessSearchRow> rows, string applicationId, string applicationName, string tenantName)
    {
        return rows.Select(row =>
        {
            bool isContextEntity = row.EntityType is "APPLICATION" or "TENANT";
            AccessSearchRow stamped = isContextEntity
                ? row
                : row with
                {
                    ApplicationId = string.IsNullOrEmpty(row.ApplicationId) ? applicationId : row.ApplicationId,
                    ApplicationName = string.IsNullOrEmpty(row.ApplicationName) ? applicationName : row.ApplicationName,
                    TenantName = string.IsNullOrEmpty(row.TenantName) ? tenantName : row.TenantName,
                };

            return stamped.Children is { Count: > 0 } kids
                ? stamped with { Children = StampApplicationContext(kids, applicationId, applicationName, tenantName) }
                : stamped;
        }).ToList();
    }

    /// <summary>
    /// The authoritative platform-wide total of records matching <paramref name="spec"/> across every
    /// accessible application, as a single distinct number. Registry entities that are cross-application
    /// by nature (e.g. a tenant that owns several applications) are counted once over the full scope so
    /// they are never double-counted; the built-in per-application entities are disjoint by application,
    /// so their per-application counts are summed. This keeps a platform "how many …" answer correct
    /// rather than inflated by iterating each application, which would count a shared row once per app.
    /// </summary>
    public async Task<int> CountPlatformAsync(
        IReadOnlyList<(Guid RefId, string ApplicationId)> apps,
        AccessSearchSpec spec,
        CancellationToken cancellationToken,
        bool includePlatformScoped = false)
    {
        if (Validate(spec) is not null || apps.Count == 0)
        {
            return 0;
        }

        // Registry entities accept the full multi-application scope directly, so a single query returns
        // the distinct total with no cross-application duplication.
        if (!Schema.ContainsKey(spec.Entity) && Registry.TryGet(spec.Entity, out IRegisteredAccessEntity descriptor))
        {
            var scope = new AccessScope(
                apps.Select(a => a.RefId).ToList(),
                apps.Select(a => a.ApplicationId).ToList(),
                includePlatformScoped);
            return await descriptor.CountAsync(dbContext, scope, spec, cancellationToken);
        }

        // Built-in entities are partitioned by application; sum the disjoint per-application counts.
        int total = 0;
        foreach ((Guid refId, string applicationId) in apps)
        {
            var scope = new AccessScope([refId], [applicationId]);
            total += await CountEntityAsync(refId, applicationId, scope, spec, cancellationToken);
        }

        return total;
    }

    /// <summary>
    /// True when <paramref name="entity"/> is a generic registry entity rather than one of the five
    /// application-partitioned built-ins (role, permission, policy, assignment, subject). Registry
    /// entities such as tenant and application can relate to rows spanning several applications, so a
    /// platform-wide relationship count over them must be evaluated across the whole accessible scope
    /// in one pass rather than iterated one application at a time.
    /// </summary>
    public static bool IsCrossApplicationEntity(string entity) =>
        !string.IsNullOrWhiteSpace(entity)
        && !Schema.ContainsKey(entity)
        && Registry.TryGet(entity, out _);

    /// <summary>
    /// Executes a relationship "group" (count children per parent) for a cross-application entity over
    /// the full accessible scope in a single pass. A tenant owns applications across several
    /// application scopes, so counting inside a per-application loop would cap each parent at the
    /// children visible in that one iteration — a tenant owning three applications would report one.
    /// The scope stays the caller's accessible applications, so the security boundary is unchanged;
    /// this only widens the count from a single application to all of them at once. Mirrors
    /// <see cref="CountPlatformAsync"/>, which already counts cross-application entities over the full scope.
    /// </summary>
    public async Task<AccessSearchOutcome> ExecuteGroupPlatformAsync(
        IReadOnlyList<(Guid RefId, string ApplicationId)> apps,
        AccessSearchSpec spec,
        CancellationToken cancellationToken,
        bool includePlatformScoped = false)
    {
        string? validationError = Validate(spec);
        if (validationError is not null)
        {
            return new AccessSearchOutcome(false, validationError, spec.Entity, []);
        }

        if (string.IsNullOrWhiteSpace(spec.GroupByRelationship) || apps.Count == 0)
        {
            return new AccessSearchOutcome(true, null, spec.Entity, [], AccessSearchModes.Group);
        }

        // The generic relationship-count fallback resolves both the parent rows and their children
        // within this scope, so passing every accessible application yields one row per parent with
        // its true cross-application child count. (appRefId/applicationId are unused for registry
        // entities, whose queries are scope-driven.)
        var scope = new AccessScope(
            apps.Select(a => a.RefId).ToList(),
            apps.Select(a => a.ApplicationId).ToList(),
            includePlatformScoped);
        List<AccessSearchRow> rows = await QueryRelationshipCountsAsync(
            Guid.Empty, string.Empty, scope, spec.Entity, spec.GroupByRelationship, cancellationToken);
        return new AccessSearchOutcome(true, null, spec.Entity, rows.Take(EffectiveLimit(spec.Limit)).ToList(), AccessSearchModes.Group);
    }

    /// <summary>
    /// Executes a relationship "include" (tree: parent rows with their related children attached) for a
    /// cross-application entity over the full accessible scope in a single pass. A tenant's applications
    /// (or an application's subjects) can span several application scopes, so resolving children inside
    /// the per-application loop only ever attaches the children visible in the one application being
    /// iterated — a tenant owning three applications would show one, and every other per-application
    /// iteration's tenant row is then dropped by the merge loop's de-duplication (cross-application
    /// entities carry no application id, so they dedupe to the same key regardless of which iteration
    /// produced them). Resolving the include once over every accessible application avoids both problems.
    /// The scope stays the caller's accessible applications, so the security boundary is unchanged.
    /// Mirrors <see cref="ExecuteGroupPlatformAsync"/>.
    /// </summary>
    public async Task<AccessSearchOutcome> ExecuteIncludePlatformAsync(
        IReadOnlyList<(Guid RefId, string ApplicationId)> apps,
        AccessSearchSpec spec,
        CancellationToken cancellationToken,
        bool includePlatformScoped = false)
    {
        string? validationError = Validate(spec);
        if (validationError is not null)
        {
            return new AccessSearchOutcome(false, validationError, spec.Entity, []);
        }

        if (string.IsNullOrWhiteSpace(spec.Include) || apps.Count == 0)
        {
            return new AccessSearchOutcome(true, null, spec.Entity, [], AccessSearchModes.Tree);
        }

        var scope = new AccessScope(
            apps.Select(a => a.RefId).ToList(),
            apps.Select(a => a.ApplicationId).ToList(),
            includePlatformScoped);
        List<AccessSearchRow> parents = await QueryEntityRowsAsync(Guid.Empty, string.Empty, scope, spec, PerEntityLimit, cancellationToken);
        List<AccessSearchRow> withChildren = await AttachChildrenAsync(scope, spec.Entity, spec.Include, parents, cancellationToken);

        string? childEntityForGrandchild = null;
        if (!string.IsNullOrWhiteSpace(spec.IncludeChild))
        {
            childEntityForGrandchild = Relationships.TargetEntityFor(spec.Entity, spec.Include);
            if (childEntityForGrandchild is not null)
            {
                withChildren = await AttachGrandchildrenAsync(scope, childEntityForGrandchild, spec.IncludeChild, withChildren, cancellationToken);
            }
        }

        if (!string.IsNullOrWhiteSpace(spec.IncludeGrandchild) && childEntityForGrandchild is not null)
        {
            string? grandchildEntity = Relationships.TargetEntityFor(childEntityForGrandchild, spec.IncludeChild!);
            if (grandchildEntity is not null)
            {
                withChildren = await AttachGreatGrandchildrenAsync(scope, grandchildEntity, spec.IncludeGrandchild, withChildren, cancellationToken);
            }
        }

        return new AccessSearchOutcome(true, null, spec.Entity, withChildren.Take(EffectiveLimit(spec.Limit)).ToList(), AccessSearchModes.Tree);
    }

    /// <summary>
    /// Executes a presence / compound-absence query ("has A", "has A but not B") for a
    /// cross-application entity over the full accessible scope in a single pass. A tenant owns
    /// applications across several scopes, so evaluating "has policies but no active users" inside the
    /// per-application loop could flag a tenant that has users in one application but not another — a
    /// false positive. Resolving presence/absence once over every accessible application avoids that.
    /// The scope stays the caller's accessible applications, so the security boundary is unchanged.
    /// Mirrors <see cref="ExecuteGroupPlatformAsync"/>.
    /// </summary>
    public async Task<AccessSearchOutcome> ExecutePresenceAbsencePlatformAsync(
        IReadOnlyList<(Guid RefId, string ApplicationId)> apps,
        AccessSearchSpec spec,
        CancellationToken cancellationToken,
        bool includePlatformScoped = false)
    {
        // Validate the entity and any filters via the shared closed-schema check, with the relationship
        // fields stripped so its single-application presence/absence guard (which correctly blocks the
        // per-app path) does not fire here — cross-application evaluation is exactly what this provides.
        AccessSearchSpec filterSpec = spec with
        {
            PresentRelationship = null,
            AbsentRelationship = null,
            GroupByRelationship = null,
            Include = null,
            IncludeChild = null,
            GroupByField = null,
            Aggregate = null,
        };
        string? validationError = Validate(filterSpec);
        if (validationError is not null)
        {
            return new AccessSearchOutcome(false, validationError, spec.Entity, []);
        }

        bool hasPresent = !string.IsNullOrWhiteSpace(spec.PresentRelationship);
        bool hasAbsent = !string.IsNullOrWhiteSpace(spec.AbsentRelationship);
        if ((!hasPresent && !hasAbsent) || apps.Count == 0)
        {
            return new AccessSearchOutcome(true, null, spec.Entity, [], AccessSearchModes.Records);
        }

        // Closed allow-list: each named relationship must be registered for the entity.
        if (hasPresent && !Relationships.Has(spec.Entity, spec.PresentRelationship!))
        {
            return new AccessSearchOutcome(false, $"Unknown relationship '{spec.PresentRelationship}' for entity '{spec.Entity}'. Allowed: {RelationshipList(spec.Entity)}.", spec.Entity, []);
        }

        if (hasAbsent && !Relationships.Has(spec.Entity, spec.AbsentRelationship!))
        {
            return new AccessSearchOutcome(false, $"Unknown relationship '{spec.AbsentRelationship}' for entity '{spec.Entity}'. Allowed: {RelationshipList(spec.Entity)}.", spec.Entity, []);
        }

        var scope = new AccessScope(
            apps.Select(a => a.RefId).ToList(),
            apps.Select(a => a.ApplicationId).ToList(),
            includePlatformScoped);
        List<AccessSearchRow> parents = await QueryEntityRowsAsync(Guid.Empty, string.Empty, scope, filterSpec, PerEntityLimit, cancellationToken);
        List<AccessSearchRow> matches = await FilterByPresenceAbsenceAsync(scope, spec.Entity, parents, spec.PresentRelationship, spec.AbsentRelationship, cancellationToken);
        return new AccessSearchOutcome(true, null, spec.Entity, matches.Take(EffectiveLimit(spec.Limit)).ToList(), AccessSearchModes.Records);
    }

    /// <summary>
    /// Executes a field group-by (breakdown by an allow-listed low-cardinality field) over the full
    /// accessible scope in a single GROUP BY, so counts are correct across one or many applications
    /// without any per-application merge. Mirrors <see cref="ExecuteGroupPlatformAsync"/>.
    /// </summary>
    public async Task<AccessSearchOutcome> ExecuteFieldGroupPlatformAsync(
        IReadOnlyList<(Guid RefId, string ApplicationId)> apps,
        AccessSearchSpec spec,
        CancellationToken cancellationToken,
        bool includePlatformScoped = false)
    {
        string? validationError = Validate(spec);
        if (validationError is not null)
        {
            return new AccessSearchOutcome(false, validationError, spec.Entity, []);
        }

        if (string.IsNullOrWhiteSpace(spec.GroupByField) || apps.Count == 0)
        {
            return new AccessSearchOutcome(true, null, spec.Entity, [], AccessSearchModes.Group);
        }

        var scope = new AccessScope(
            apps.Select(a => a.RefId).ToList(),
            apps.Select(a => a.ApplicationId).ToList(),
            includePlatformScoped);
        List<AccessSearchRow> rows = !string.IsNullOrWhiteSpace(spec.GroupBySecondaryField)
            ? await QueryFieldGroupNestedAsync(scope, spec.Entity, spec.GroupByField, spec.GroupBySecondaryField, cancellationToken)
            : await QueryFieldGroupAsync(scope, spec.Entity, spec.GroupByField, cancellationToken);
        return new AccessSearchOutcome(true, null, spec.Entity, rows.Take(EffectiveLimit(spec.Limit)).ToList(), AccessSearchModes.Group);
    }

    /// <summary>
    /// Executes a plain records lookup or scalar count for a cross-application entity over the full
    /// accessible scope in a single pass, rather than the per-application loop. Some cross-application
    /// entities (e.g. AI invocation/prompt-log rows, which record platform-wide Ask AI activity) carry
    /// no owning application at all, so the per-application loop's single-app scope never sees them —
    /// every iteration is scoped to one application and such rows silently vanish. The scope stays the
    /// caller's accessible applications (plus platform-scoped rows when permitted), so the security
    /// boundary is unchanged; this only widens visibility from "whatever one application's iteration
    /// happens to see" to the whole accessible scope evaluated once. Mirrors
    /// <see cref="ExecuteGroupPlatformAsync"/>.
    /// </summary>
    public async Task<AccessSearchOutcome> ExecuteRecordsPlatformAsync(
        IReadOnlyList<(Guid RefId, string ApplicationId)> apps,
        AccessSearchSpec spec,
        CancellationToken cancellationToken,
        bool includePlatformScoped = false)
    {
        string? validationError = Validate(spec);
        if (validationError is not null)
        {
            return new AccessSearchOutcome(false, validationError, spec.Entity, []);
        }

        if (apps.Count == 0)
        {
            return new AccessSearchOutcome(true, null, spec.Entity, [], AccessSearchModes.Records);
        }

        var scope = new AccessScope(
            apps.Select(a => a.RefId).ToList(),
            apps.Select(a => a.ApplicationId).ToList(),
            includePlatformScoped);

        if (spec.Aggregate is not null && spec.Aggregate.Equals("count", StringComparison.OrdinalIgnoreCase))
        {
            List<AccessSearchRow> sample = await QueryEntityRowsAsync(Guid.Empty, string.Empty, scope, spec, PerEntityLimit, cancellationToken);
            int total = await CountEntityAsync(Guid.Empty, string.Empty, scope, spec, cancellationToken);
            AccessSearchRow countRow = CountRow(string.Empty, spec.Entity, total) with { Children = sample.Take(PerEntityLimit).ToList() };
            return new AccessSearchOutcome(true, null, spec.Entity, [countRow], AccessSearchModes.Count);
        }

        List<AccessSearchRow> rows = await QueryEntityRowsAsync(Guid.Empty, string.Empty, scope, spec, PerEntityLimit, cancellationToken);
        rows = rows.Take(EffectiveLimit(spec.Limit)).ToList();
        return new AccessSearchOutcome(true, null, spec.Entity, rows, AccessSearchModes.Records);
    }

    private static bool CanGroupByField(string entity, string field) =>
        !string.IsNullOrWhiteSpace(entity)
        && GroupableFields.TryGetValue(entity, out string[]? fields)
        && fields.Contains(field, StringComparer.OrdinalIgnoreCase);

    private static string GroupableFieldList(string entity) =>
        GroupableFields.TryGetValue(entity, out string[]? fields) ? string.Join(", ", fields) : "(none)";

    // Counts an entity's rows by a low-cardinality field value across the given scope: one Group row
    // per value (ordered by count desc, capped). A single scoped GROUP BY, so it is correct over one
    // or many applications alike. Unsupported entity/field returns no rows (validation rejects those).
    private async Task<List<AccessSearchRow>> QueryFieldGroupAsync(
        AccessScope scope, string entity, string field, CancellationToken ct)
    {
        string e = entity.ToLowerInvariant();
        string f = field.ToLowerInvariant();
        List<(string Key, int Count)> groups = (e, f) switch
        {
            ("role", "risklevel") => await GroupCountAsync(dbContext.Roles.AsNoTracking()
                .Where(r => scope.AppRefIds.Contains(r.ApplicationRefId)), r => r.RiskLevel, ct),
            ("role", "status") => await GroupCountAsync(dbContext.Roles.AsNoTracking()
                .Where(r => scope.AppRefIds.Contains(r.ApplicationRefId)), r => r.Status, ct),
            ("permission", "risklevel") => await GroupCountAsync(dbContext.Permissions.AsNoTracking()
                .Where(p => scope.AppRefIds.Contains(p.ApplicationRefId)), p => p.RiskLevel, ct),
            ("permission", "status") => await GroupCountAsync(dbContext.Permissions.AsNoTracking()
                .Where(p => scope.AppRefIds.Contains(p.ApplicationRefId)), p => p.Status, ct),
            ("policy", "effect") => await GroupCountAsync(dbContext.Policies.AsNoTracking()
                .Where(p => scope.AppRefIds.Contains(p.ApplicationRefId)), p => p.Effect, ct),
            ("policy", "state") => await GroupCountAsync(dbContext.Policies.AsNoTracking()
                .Where(p => scope.AppRefIds.Contains(p.ApplicationRefId)), p => p.State, ct),
            ("assignment", "state") => await GroupCountAsync(dbContext.Assignments.AsNoTracking()
                .Where(a => scope.AppRefIds.Contains(a.ApplicationRefId)), a => a.State, ct),
            _ => [],
        };

        string entityType = e.ToUpperInvariant();
        string plural = e == "policy" ? "policies" : e + "s";
        return groups
            .Select(g => new AccessSearchRow(
                string.Empty,
                entityType,
                g.Key.Length == 0 ? "(none)" : g.Key,
                $"{g.Count} {(g.Count == 1 ? e : plural)}",
                null,
                null))
            .ToList();
    }

    // Two-level field group (ASKAI-C5 cross-tab): counts each entity by two low-cardinality fields at
    // once, nested primary → secondary. A single scoped GROUP BY over the entity's two groupable fields,
    // arranged by which field is primary, so it is correct across one or many applications and bounded
    // (both fields are low-cardinality). Outer rows carry the group total; inner rows the per-secondary
    // count. Unsupported entity/field returns no rows (validation rejects those first).
    private async Task<List<AccessSearchRow>> QueryFieldGroupNestedAsync(
        AccessScope scope, string entity, string primaryField, string secondaryField, CancellationToken ct)
    {
        string e = entity.ToLowerInvariant();
        string p1 = primaryField.ToLowerInvariant();
        string p2 = secondaryField.ToLowerInvariant();
        bool Pair(string a, string b) => (p1 == a && p2 == b) || (p1 == b && p2 == a);

        (string Field1, string Field2, List<(string F1, string F2, int Count)> Raw) grouped = (e, p1, p2) switch
        {
            // "role" has three low-cardinality groupable fields (riskLevel, status, privileged), so all
            // three pairings are supported; the other entities have exactly two, so only one pairing.
            ("role", _, _) when Pair("risklevel", "status") => ("riskLevel", "status", (await dbContext.Roles.AsNoTracking()
                .Where(r => scope.AppRefIds.Contains(r.ApplicationRefId))
                .GroupBy(r => new { r.RiskLevel, r.Status })
                .Select(g => new { g.Key.RiskLevel, g.Key.Status, Count = g.Count() })
                .ToListAsync(ct))
                .Select(x => (x.RiskLevel ?? string.Empty, x.Status ?? string.Empty, x.Count)).ToList()),
            ("role", _, _) when Pair("privileged", "status") => ("privileged", "status", (await dbContext.Roles.AsNoTracking()
                .Where(r => scope.AppRefIds.Contains(r.ApplicationRefId))
                .GroupBy(r => new { r.Privileged, r.Status })
                .Select(g => new { g.Key.Privileged, g.Key.Status, Count = g.Count() })
                .ToListAsync(ct))
                .Select(x => (x.Privileged ? "true" : "false", x.Status ?? string.Empty, x.Count)).ToList()),
            ("role", _, _) when Pair("privileged", "risklevel") => ("privileged", "riskLevel", (await dbContext.Roles.AsNoTracking()
                .Where(r => scope.AppRefIds.Contains(r.ApplicationRefId))
                .GroupBy(r => new { r.Privileged, r.RiskLevel })
                .Select(g => new { g.Key.Privileged, g.Key.RiskLevel, Count = g.Count() })
                .ToListAsync(ct))
                .Select(x => (x.Privileged ? "true" : "false", x.RiskLevel ?? string.Empty, x.Count)).ToList()),
            ("permission", _, _) => ("riskLevel", "status", (await dbContext.Permissions.AsNoTracking()
                .Where(p => scope.AppRefIds.Contains(p.ApplicationRefId))
                .GroupBy(p => new { p.RiskLevel, p.Status })
                .Select(g => new { g.Key.RiskLevel, g.Key.Status, Count = g.Count() })
                .ToListAsync(ct))
                .Select(x => (x.RiskLevel ?? string.Empty, x.Status ?? string.Empty, x.Count)).ToList()),
            ("policy", _, _) => ("effect", "state", (await dbContext.Policies.AsNoTracking()
                .Where(p => scope.AppRefIds.Contains(p.ApplicationRefId))
                .GroupBy(p => new { p.Effect, p.State })
                .Select(g => new { g.Key.Effect, g.Key.State, Count = g.Count() })
                .ToListAsync(ct))
                .Select(x => (x.Effect ?? string.Empty, x.State ?? string.Empty, x.Count)).ToList()),
            _ => (string.Empty, string.Empty, new List<(string F1, string F2, int Count)>()),
        };

        bool primaryIsFirst = string.Equals(primaryField, grouped.Field1, StringComparison.OrdinalIgnoreCase);
        string entityType = e.ToUpperInvariant();
        string plural = e == "policy" ? "policies" : e + "s";
        string Label(int n) => $"{n} {(n == 1 ? e : plural)}";
        string Show(string v) => v.Length == 0 ? "(none)" : v;

        return grouped.Raw
            .Select(x => primaryIsFirst ? (Primary: x.F1, Secondary: x.F2, x.Count) : (Primary: x.F2, Secondary: x.F1, x.Count))
            .GroupBy(x => x.Primary)
            .Select(g => new
            {
                Primary = g.Key,
                Total = g.Sum(y => y.Count),
                Inner = g.OrderByDescending(y => y.Count).ThenBy(y => y.Secondary, StringComparer.Ordinal).ToList(),
            })
            .OrderByDescending(o => o.Total)
            .ThenBy(o => o.Primary, StringComparer.Ordinal)
            .Take(PerEntityLimit)
            .Select(o => new AccessSearchRow(
                string.Empty, entityType, Show(o.Primary), Label(o.Total), null, null,
                o.Inner.Select(i => new AccessSearchRow(
                    string.Empty, entityType, Show(i.Secondary), Label(i.Count), null, null)).ToList()))
            .ToList();
    }

    // Runs a scoped GROUP BY on a single string column and returns (value, count), ordered by count
    // desc then value, capped at PerEntityLimit. Ordering/capping happen in memory after the grouped
    // fetch so the query translates identically on Npgsql and the EF in-memory provider used by tests.
    private static async Task<List<(string Key, int Count)>> GroupCountAsync<TEntity>(
        IQueryable<TEntity> source,
        Expression<Func<TEntity, string>> keySelector,
        CancellationToken ct)
    {
        var grouped = await source
            .GroupBy(keySelector)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return grouped
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Take(PerEntityLimit)
            .Select(x => (x.Key ?? string.Empty, x.Count))
            .ToList();
    }

    /// <summary>
    /// Resolves each filter value against the live data for the given applications so a query never
    /// executes with a value the model invented or mis-cased. <c>enum</c>/<c>bool</c> values are
    /// canonicalised to their allowed domain; <c>ref</c> values (role/permission/resource/action/
    /// resourceId keys) are matched to real stored values — exact first, then a single unambiguous
    /// fuzzy match. When a value cannot be resolved confidently the method returns a human-readable
    /// reason so the caller can respond with guidance instead of misleading or empty results. Plain
    /// string, date and <c>contains</c> (explicit substring) filters pass through unchanged.
    /// Correctness is enforced here, on the server — never by what happened to fit in the prompt.
    /// </summary>
    public async Task<AccessSearchValueResolution> ResolveFilterValuesAsync(
        IReadOnlyCollection<Guid> applicationRefIds,
        AccessSearchSpec spec,
        CancellationToken cancellationToken)
    {
        // Registry entities validate and resolve their own reflection-derived fields; core-entity
        // filters are the ones we canonicalise here.
        if (!Schema.ContainsKey(spec.Entity) || spec.Filters.Count == 0 || applicationRefIds.Count == 0)
        {
            return AccessSearchValueResolution.Ok(spec);
        }

        var resolvedFilters = new List<AccessSearchFilter>(spec.Filters.Count);
        foreach (AccessSearchFilter filter in spec.Filters)
        {
            // "contains" is an explicit substring search — honor it literally, never canonicalise.
            if (filter.Operator.Equals("contains", StringComparison.OrdinalIgnoreCase))
            {
                resolvedFilters.Add(filter);
                continue;
            }

            AccessSearchFieldSchema meta = FieldSchemaFor(spec.Entity, filter.Field);
            switch (meta.Type)
            {
                case "enum":
                case "bool":
                {
                    string? error = ResolveEnumValue(filter, meta, out string canonical);
                    if (error is not null)
                    {
                        return AccessSearchValueResolution.Fail(error);
                    }

                    resolvedFilters.Add(filter with { Value = canonical });
                    break;
                }

                case "ref":
                {
                    (string? canonical, string? error) = await ResolveRefValueAsync(applicationRefIds, filter, cancellationToken);
                    if (error is not null)
                    {
                        return AccessSearchValueResolution.Fail(error);
                    }

                    resolvedFilters.Add(filter with { Value = canonical! });
                    break;
                }

                default:
                    resolvedFilters.Add(filter);
                    break;
            }
        }

        return AccessSearchValueResolution.Ok(spec with { Filters = resolvedFilters });
    }

    // Canonicalises an enum/bool value (or comma-separated set for "in") against the field's allowed
    // domain. Returns null on success (with the canonical value in <paramref name="canonical"/>) or a
    // reason string when a value is not a confident match.
    private static string? ResolveEnumValue(AccessSearchFilter filter, AccessSearchFieldSchema meta, out string canonical)
    {
        canonical = filter.Value;
        string[] allowed = meta.Values is { Count: > 0 } ? [.. meta.Values] : [];
        if (allowed.Length == 0)
        {
            return null;
        }

        string[] parts = filter.Operator.Equals("in", StringComparison.OrdinalIgnoreCase)
            ? filter.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [filter.Value.Trim()];
        if (parts.Length == 0)
        {
            return null;
        }

        var result = new List<string>();
        foreach (string part in parts)
        {
            string? match = MatchDomain(part, allowed);
            if (match is null)
            {
                return $"\"{part}\" is not a valid {filter.Field} value. Allowed values are: {string.Join(", ", allowed)}.";
            }

            if (!result.Contains(match, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(match);
            }
        }

        canonical = string.Join(",", result);
        return null;
    }

    // Matches a value to a single allowed domain member: exact (case-insensitive), else a unique
    // prefix, else a unique substring. Ambiguous or absent → null (caller reports guidance).
    private static string? MatchDomain(string value, string[] allowed)
    {
        string v = value.Trim();

        string? exact = allowed.FirstOrDefault(a => a.Equals(v, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        List<string> prefix = [.. allowed.Where(a => a.StartsWith(v, StringComparison.OrdinalIgnoreCase))];
        if (prefix.Count == 1)
        {
            return prefix[0];
        }

        List<string> contains = [.. allowed.Where(a => a.Contains(v, StringComparison.OrdinalIgnoreCase))];
        return contains.Count == 1 ? contains[0] : null;
    }

    // Resolves a ref value (or comma-separated set) against the real stored keys for the field.
    private async Task<(string? Value, string? Error)> ResolveRefValueAsync(
        IReadOnlyCollection<Guid> applicationRefIds, AccessSearchFilter filter, CancellationToken cancellationToken)
    {
        IQueryable<string>? source = RefValueSource(applicationRefIds, filter.Field);
        if (source is null)
        {
            return (filter.Value, null);
        }

        string[] parts = filter.Operator.Equals("in", StringComparison.OrdinalIgnoreCase)
            ? filter.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [filter.Value.Trim()];
        if (parts.Length == 0)
        {
            return (filter.Value, null);
        }

        var canonical = new List<string>();
        foreach (string part in parts)
        {
            (string? value, string? error) = await ResolveSingleRefAsync(source, filter.Field, part, cancellationToken);
            if (error is not null)
            {
                return (null, error);
            }

            if (!canonical.Contains(value!, StringComparer.OrdinalIgnoreCase))
            {
                canonical.Add(value!);
            }
        }

        return (string.Join(",", canonical), null);
    }

    private static async Task<(string? Value, string? Error)> ResolveSingleRefAsync(
        IQueryable<string> source, string field, string rawValue, CancellationToken cancellationToken)
    {
        string value = rawValue.Trim();
        string lower = value.ToLowerInvariant();

        // Exact case-insensitive match — return the canonical stored spelling.
        string? exact = await source.Where(x => x.ToLower() == lower).FirstOrDefaultAsync(cancellationToken);
        if (exact is not null)
        {
            return (exact, null);
        }

        // Fuzzy substring match — accept only when it is unambiguous.
        List<string> near = await source.Where(x => x.ToLower().Contains(lower)).Distinct().Take(6).ToListAsync(cancellationToken);
        if (near.Count == 1)
        {
            return (near[0], null);
        }

        return near.Count > 1
            ? (null, $"\"{value}\" matches several {field} values. Did you mean: {string.Join(", ", near.Take(5))}?")
            : (null, $"No {field} matching \"{value}\" exists in the applications in scope.");
    }

    // Maps a ref field to the live column it must resolve against, scoped to the given applications.
    private IQueryable<string>? RefValueSource(IReadOnlyCollection<Guid> applicationRefIds, string field) =>
        field.ToLowerInvariant() switch
        {
            "rolekey" or "role" => dbContext.Roles.AsNoTracking()
                .Where(r => applicationRefIds.Contains(r.ApplicationRefId)).Select(r => r.RoleKey),
            "permissionkey" or "permission" => dbContext.Permissions.AsNoTracking()
                .Where(p => applicationRefIds.Contains(p.ApplicationRefId)).Select(p => p.PermissionKey),
            "resource" => dbContext.Permissions.AsNoTracking()
                .Where(p => applicationRefIds.Contains(p.ApplicationRefId)).Select(p => p.Resource),
            "action" => dbContext.Permissions.AsNoTracking()
                .Where(p => applicationRefIds.Contains(p.ApplicationRefId)).Select(p => p.Action),
            "resourceid" => dbContext.Assignments.AsNoTracking()
                .Where(a => applicationRefIds.Contains(a.ApplicationRefId) && a.ResourceId != null).Select(a => a.ResourceId!),
            _ => null,
        };


    // Routes an entity query to the generic registry descriptor or the built-in hand-written query.
    private async Task<List<AccessSearchRow>> QueryEntityRowsAsync(
        Guid appRefId, string applicationId, AccessScope scope, AccessSearchSpec spec, int limit, CancellationToken ct)
    {
        if (!Schema.ContainsKey(spec.Entity) && Registry.TryGet(spec.Entity, out IRegisteredAccessEntity descriptor))
        {
            return await descriptor.QueryAsync(dbContext, scope, spec, limit, ct);
        }

        return spec.Entity.ToLowerInvariant() switch
        {
            "role" => await QueryRolesAsync(appRefId, applicationId, spec, ct),
            "permission" => await QueryPermissionsAsync(appRefId, applicationId, spec, ct),
            "policy" => await QueryPoliciesAsync(appRefId, applicationId, spec, ct),
            "assignment" => await QueryAssignmentsAsync(appRefId, applicationId, spec, ct),
            "subject" => await QuerySubjectsAsync(appRefId, applicationId, spec, ct),
            _ => [],
        };
    }

    private async Task<int> CountEntityAsync(
        Guid appRefId, string applicationId, AccessScope scope, AccessSearchSpec spec, CancellationToken ct)
    {
        if (!Schema.ContainsKey(spec.Entity) && Registry.TryGet(spec.Entity, out IRegisteredAccessEntity descriptor))
        {
            return await descriptor.CountAsync(dbContext, scope, spec, ct);
        }

        // Built-in entity rows are capped at PerEntityLimit; the count reflects that same bound.
        List<AccessSearchRow> rows = await QueryEntityRowsAsync(appRefId, applicationId, scope, spec, PerEntityLimit, ct);
        return rows.Count;
    }

    // Attaches each parent's related children (keyed by natural key) for an include (tree) result.
    private async Task<List<AccessSearchRow>> AttachChildrenAsync(
        AccessScope scope, string entity, string relationship, List<AccessSearchRow> parents, CancellationToken ct)
    {
        if (parents.Count == 0)
        {
            return parents;
        }

        List<string> parentKeys = parents.Select(p => p.DeepLinkKey ?? p.Title).Distinct(StringComparer.Ordinal).ToList();
        IReadOnlyDictionary<string, List<AccessSearchRow>> childrenByKey =
            await Relationships.ResolveAsync(entity, relationship, dbContext, scope, parentKeys, ChildPerParentLimit, ct);

        return parents
            .Select(p =>
            {
                List<AccessSearchRow> kids = childrenByKey.TryGetValue(p.DeepLinkKey ?? p.Title, out List<AccessSearchRow>? list) ? list : [];
                // Children carry no application id of their own; inherit the parent's so app-scoped
                // deep links (role/permission/policy) resolve correctly.
                List<AccessSearchRow> scopedKids = kids
                    .Select(c => string.IsNullOrEmpty(c.ApplicationId) ? c with { ApplicationId = p.ApplicationId } : c)
                    .ToList();
                return p with { Children = scopedKids };
            })
            .ToList();
    }

    // Attaches a bounded second level of children (ASKAI-C2): for every already-attached child, resolve
    // its own relationship one hop further and nest the results under it. All child keys are resolved in
    // a single scoped query capped at ChildPerParentLimit per child (and FetchCap overall in the
    // resolver), so the extra depth stays bounded. Read-only.
    private async Task<List<AccessSearchRow>> AttachGrandchildrenAsync(
        AccessScope scope, string childEntity, string relationship, List<AccessSearchRow> parents, CancellationToken ct)
    {
        List<string> childKeys = parents
            .SelectMany(p => p.Children ?? [])
            .Select(c => c.DeepLinkKey ?? c.Title)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (childKeys.Count == 0)
        {
            return parents;
        }

        IReadOnlyDictionary<string, List<AccessSearchRow>> grandchildrenByKey =
            await Relationships.ResolveAsync(childEntity, relationship, dbContext, scope, childKeys, ChildPerParentLimit, ct);

        return parents
            .Select(p => p with
            {
                Children = (p.Children ?? []).Select(c =>
                {
                    List<AccessSearchRow> gks = grandchildrenByKey.TryGetValue(c.DeepLinkKey ?? c.Title, out List<AccessSearchRow>? list) ? list : [];
                    // Grandchildren carry no application id of their own; inherit the child's so
                    // app-scoped deep links resolve correctly.
                    List<AccessSearchRow> scoped = gks
                        .Select(g => string.IsNullOrEmpty(g.ApplicationId) ? g with { ApplicationId = c.ApplicationId } : g)
                        .ToList();
                    return c with { Children = scoped };
                }).ToList(),
            })
            .ToList();
    }

    // Attaches a bounded third level of children (C2 extended): for every already-attached
    // grandchild, resolve its own relationship one hop further and nest the results under it.
    // Mirrors AttachGrandchildrenAsync one hop deeper — all grandchild keys are resolved in a single
    // scoped query capped at ChildPerParentLimit per grandchild (and FetchCap overall), so the extra
    // depth stays bounded. Read-only.
    private async Task<List<AccessSearchRow>> AttachGreatGrandchildrenAsync(
        AccessScope scope, string grandchildEntity, string relationship, List<AccessSearchRow> parents, CancellationToken ct)
    {
        List<string> grandchildKeys = parents
            .SelectMany(p => p.Children ?? [])
            .SelectMany(c => c.Children ?? [])
            .Select(g => g.DeepLinkKey ?? g.Title)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (grandchildKeys.Count == 0)
        {
            return parents;
        }

        IReadOnlyDictionary<string, List<AccessSearchRow>> greatGrandchildrenByKey =
            await Relationships.ResolveAsync(grandchildEntity, relationship, dbContext, scope, grandchildKeys, ChildPerParentLimit, ct);

        return parents
            .Select(p => p with
            {
                Children = (p.Children ?? []).Select(c => c with
                {
                    Children = (c.Children ?? []).Select(g =>
                    {
                        List<AccessSearchRow> ggks = greatGrandchildrenByKey.TryGetValue(g.DeepLinkKey ?? g.Title, out List<AccessSearchRow>? list) ? list : [];
                        // Great-grandchildren carry no application id of their own; inherit the
                        // grandchild's so app-scoped deep links resolve correctly.
                        List<AccessSearchRow> scoped = ggks
                            .Select(gg => string.IsNullOrEmpty(gg.ApplicationId) ? gg with { ApplicationId = g.ApplicationId } : gg)
                            .ToList();
                        return g with { Children = scoped };
                    }).ToList(),
                }).ToList(),
            })
            .ToList();
    }

    // A single scalar-count result row, reusing the standard row shape (no deep link).
    private static AccessSearchRow CountRow(string applicationId, string entity, int count) =>
        new(applicationId, "COUNT", count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"{entity} record(s) matching the query", null, null);

    // Splits a filter value into a lowered set for the "in" (set-membership) operator, e.g.
    // "disabled,deprecated" → ["disabled", "deprecated"].
    private static string[] SplitValues(string? value) =>
        (value ?? string.Empty).ToLowerInvariant()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Parses a filter value into an instant for date comparisons. Accepts ISO-8601 or the literal
    // "now" (evaluated server-side, so the model never needs to know the current time). Returns null
    // when unparseable, so the caller can simply skip the filter rather than guess.
    private static DateTimeOffset? ParseInstant(string? value)
    {
        string v = (value ?? string.Empty).Trim();
        if (v.Length == 0)
        {
            return null;
        }

        if (v.Equals("now", StringComparison.OrdinalIgnoreCase) || v.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            return DateTimeOffset.UtcNow;
        }

        return DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset dt)
            ? dt
            : null;
    }

    private static string? Validate(AccessSearchSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Entity))
        {
            return $"Unknown search entity '{spec.Entity}'. Allowed: {string.Join(", ", AllEntityNames())}.";
        }

        // A relationship count (groupBy) or include is only valid over a registered relationship.
        if (!string.IsNullOrWhiteSpace(spec.GroupByRelationship) && !Relationships.Has(spec.Entity, spec.GroupByRelationship))
        {
            return $"Unknown relationship '{spec.GroupByRelationship}' for entity '{spec.Entity}'. Allowed: {RelationshipList(spec.Entity)}.";
        }

        if (!string.IsNullOrWhiteSpace(spec.Include) && !Relationships.Has(spec.Entity, spec.Include))
        {
            return $"Unknown relationship '{spec.Include}' for entity '{spec.Entity}'. Allowed: {RelationshipList(spec.Entity)}.";
        }

        // A nested include ("include the child's children", C2) requires a valid first-level include and
        // a relationship that exists on that include's target (child) entity. The chain is closed: the
        // second hop can only traverse a relationship the child entity actually declares.
        if (!string.IsNullOrWhiteSpace(spec.IncludeChild))
        {
            if (string.IsNullOrWhiteSpace(spec.Include))
            {
                return "A nested include (includeChild) requires an include relationship to expand.";
            }

            string? childEntity = Relationships.TargetEntityFor(spec.Entity, spec.Include);
            if (childEntity is null || !Relationships.Has(childEntity, spec.IncludeChild))
            {
                return $"Unknown nested relationship '{spec.IncludeChild}' for '{spec.Entity}.{spec.Include}'. Allowed: {(childEntity is null ? "(none)" : RelationshipList(childEntity))}.";
            }
        }

        // A third-level nested include ("include the grandchild's children", C2 extended) requires a
        // valid two-level chain (include + includeChild) and a relationship that exists on the
        // includeChild's target (grandchild) entity. Same closed-chain rule as includeChild: the third
        // hop can only traverse a relationship the grandchild entity actually declares.
        if (!string.IsNullOrWhiteSpace(spec.IncludeGrandchild))
        {
            if (string.IsNullOrWhiteSpace(spec.IncludeChild) || string.IsNullOrWhiteSpace(spec.Include))
            {
                return "A third-level nested include (includeGrandchild) requires includeChild to expand.";
            }

            string? childEntity = Relationships.TargetEntityFor(spec.Entity, spec.Include);
            string? grandchildEntity = childEntity is null ? null : Relationships.TargetEntityFor(childEntity, spec.IncludeChild);
            if (grandchildEntity is null || !Relationships.Has(grandchildEntity, spec.IncludeGrandchild))
            {
                return $"Unknown nested relationship '{spec.IncludeGrandchild}' for '{spec.Entity}.{spec.Include}.{spec.IncludeChild}'. Allowed: {(grandchildEntity is null ? "(none)" : RelationshipList(grandchildEntity))}.";
            }
        }

        // An absence query ("has none") is only valid over a registered relationship, and only for an
        // application-partitioned entity whose rows each belong to exactly one application. Evaluating
        // absence for a cross-application or derived entity (tenant/application/subject) inside the
        // per-application loop could flag a row as "has none" in one application while it has children
        // in another — a false positive. Those are rejected here (deferred to a future compound phase).
        if (!string.IsNullOrWhiteSpace(spec.AbsentRelationship))
        {
            if (!Relationships.Has(spec.Entity, spec.AbsentRelationship))
            {
                return $"Unknown relationship '{spec.AbsentRelationship}' for entity '{spec.Entity}'. Allowed: {RelationshipList(spec.Entity)}.";
            }

            if (!Schema.ContainsKey(spec.Entity) || string.Equals(spec.Entity, "subject", StringComparison.OrdinalIgnoreCase))
            {
                return $"Absence (\"has none\") queries are not supported for entity '{spec.Entity}' yet.";
            }
        }

        // A presence / compound-absence query ("has A", or "has A but not B", C3) needs a registered
        // relationship on an application-partitioned entity — same per-application-correctness rule as
        // absence. Any accompanying AbsentRelationship is validated by the block above.
        if (!string.IsNullOrWhiteSpace(spec.PresentRelationship))
        {
            if (!Relationships.Has(spec.Entity, spec.PresentRelationship))
            {
                return $"Unknown relationship '{spec.PresentRelationship}' for entity '{spec.Entity}'. Allowed: {RelationshipList(spec.Entity)}.";
            }

            if (!Schema.ContainsKey(spec.Entity) || string.Equals(spec.Entity, "subject", StringComparison.OrdinalIgnoreCase))
            {
                return $"Presence (\"has\") queries are not supported for entity '{spec.Entity}' yet.";
            }
        }

        // A field group-by is only valid over an allow-listed low-cardinality field of the entity.
        if (!string.IsNullOrWhiteSpace(spec.GroupByField) && !CanGroupByField(spec.Entity, spec.GroupByField))
        {
            return $"Grouping by field '{spec.GroupByField}' is not supported for entity '{spec.Entity}'. Groupable fields: {GroupableFieldList(spec.Entity)}.";
        }

        // A secondary field group-by (two-level cross-tab) requires a primary groupByField, must itself
        // be an allow-listed groupable field, and must differ from the primary.
        if (!string.IsNullOrWhiteSpace(spec.GroupBySecondaryField))
        {
            if (string.IsNullOrWhiteSpace(spec.GroupByField))
            {
                return "A secondary group-by (groupBySecondaryField) requires a primary groupByField.";
            }

            if (!CanGroupByField(spec.Entity, spec.GroupBySecondaryField))
            {
                return $"Grouping by field '{spec.GroupBySecondaryField}' is not supported for entity '{spec.Entity}'. Groupable fields: {GroupableFieldList(spec.Entity)}.";
            }

            if (string.Equals(spec.GroupByField, spec.GroupBySecondaryField, StringComparison.OrdinalIgnoreCase))
            {
                return "The secondary group-by field must differ from the primary group-by field.";
            }
        }

        // Generic registry entities validate their own reflection-derived field allow-list.
        if (!Schema.ContainsKey(spec.Entity) && Registry.TryGet(spec.Entity, out IRegisteredAccessEntity descriptor))
        {
            return descriptor.Validate(spec);
        }

        if (!Schema.TryGetValue(spec.Entity, out string[]? allowedFields))
        {
            return $"Unknown search entity '{spec.Entity}'. Allowed: {string.Join(", ", AllEntityNames())}.";
        }

        foreach (AccessSearchFilter filter in spec.Filters)
        {
            if (!allowedFields.Contains(filter.Field, StringComparer.OrdinalIgnoreCase))
            {
                return $"Unknown field '{filter.Field}' for entity '{spec.Entity}'. Allowed: {string.Join(", ", allowedFields)}.";
            }

            if (!Operators.Contains(filter.Operator))
            {
                return $"Unknown operator '{filter.Operator}'. Allowed: {string.Join(", ", Operators)}.";
            }
        }

        return null;
    }

    private static IEnumerable<string> AllEntityNames() => Schema.Keys.Concat(Registry.Names);

    private static string RelationshipList(string entity)
    {
        IReadOnlyList<string> rels = Relationships.RelationshipsFor(entity);
        return rels.Count > 0 ? string.Join(", ", rels) : "(none)";
    }

    // Relationship counts (Phase 4): group a built-in entity by a related entity and count per group,
    // e.g. "how many permissions does each role grant". Read-only, scoped, capped; results deep-link
    // to the grouped entity so each count is auditable.
    private async Task<List<AccessSearchRow>> QueryRelationshipCountsAsync(
        Guid appRefId, string applicationId, AccessScope scope, string entity, string relationship, CancellationToken ct)
    {
        string e = entity.ToLowerInvariant();
        string rel = relationship.ToLowerInvariant();

        if (e == "role" && rel == "permissions")
        {
            var grouped = await (
                from rp in dbContext.RolePermissions.AsNoTracking()
                where rp.ApplicationRefId == appRefId && rp.State == "PUBLISHED"
                join role in dbContext.Roles.AsNoTracking() on rp.RoleRefId equals role.Id
                where role.Status == "ACTIVE"
                group role by role.RoleKey into g
                select new { Key = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).Take(PerEntityLimit).ToListAsync(ct);
            return grouped.Select(x => new AccessSearchRow(
                applicationId, "ROLE", x.Key, $"{x.Count} permission(s) granted", "role", x.Key)).ToList();
        }

        if (e == "role" && rel == "assignments")
        {
            var grouped = await (
                from a in dbContext.Assignments.AsNoTracking()
                where a.ApplicationRefId == appRefId && a.State == "ACTIVE"
                join role in dbContext.Roles.AsNoTracking() on a.RoleRefId equals role.Id
                group role by role.RoleKey into g
                select new { Key = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).Take(PerEntityLimit).ToListAsync(ct);
            return grouped.Select(x => new AccessSearchRow(
                applicationId, "ROLE", x.Key, $"{x.Count} active assignment(s)", "role", x.Key)).ToList();
        }

        if (e == "permission" && rel == "roles")
        {
            var grouped = await (
                from rp in dbContext.RolePermissions.AsNoTracking()
                where rp.ApplicationRefId == appRefId && rp.State == "PUBLISHED"
                join perm in dbContext.Permissions.AsNoTracking() on rp.PermissionRefId equals perm.Id
                group perm by perm.PermissionKey into g
                select new { Key = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).Take(PerEntityLimit).ToListAsync(ct);
            return grouped.Select(x => new AccessSearchRow(
                applicationId, "PERMISSION", x.Key, $"{x.Count} role(s) grant this", "permission", x.Key)).ToList();
        }

        // Generic fallback for any other registered relationship: count each parent's children via the
        // relationship resolver and return one ranked row per parent that has at least one child.
        List<AccessSearchRow> parents = await QueryEntityRowsAsync(appRefId, applicationId, scope, new AccessSearchSpec(entity, []), PerEntityLimit, ct);
        List<string> parentKeys = parents.Select(p => p.DeepLinkKey ?? p.Title).Distinct(StringComparer.Ordinal).ToList();
        IReadOnlyDictionary<string, List<AccessSearchRow>> childrenByKey =
            await Relationships.ResolveAsync(entity, relationship, dbContext, scope, parentKeys, int.MaxValue, ct);

        return parents
            .Select(p => (Parent: p, Count: childrenByKey.TryGetValue(p.DeepLinkKey ?? p.Title, out List<AccessSearchRow>? kids) ? kids.Count : 0))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .Take(PerEntityLimit)
            .Select(x => new AccessSearchRow(
                x.Parent.ApplicationId, x.Parent.EntityType, x.Parent.Title,
                $"{x.Count} {relationship}", x.Parent.DeepLinkKind, x.Parent.DeepLinkKey))
            .ToList();
    }

    // Relationship absence (ASKAI-C1): the inverse of QueryRelationshipCountsAsync — base-entity rows
    // that have ZERO of the named relationship ("roles with no assignments", "unused permissions").
    // The three built-in cases use set-based NOT EXISTS anti-joins (correct and bounded); any other
    // registered relationship resolves children for exactly the capped parent keys and keeps the
    // parents whose child count is zero. Read-only, scoped, capped.
    private async Task<List<AccessSearchRow>> QueryRelationshipAbsenceAsync(
        Guid appRefId, string applicationId, AccessScope scope, string entity, string relationship, CancellationToken ct)
    {
        string e = entity.ToLowerInvariant();
        string rel = relationship.ToLowerInvariant();

        // Roles with no active assignments.
        if (e == "role" && rel == "assignments")
        {
            List<RoleEntity> roles = await dbContext.Roles.AsNoTracking()
                .Where(r => r.ApplicationRefId == appRefId && r.Status == "ACTIVE"
                    && !dbContext.Assignments.Any(a => a.ApplicationRefId == appRefId && a.State == "ACTIVE" && a.RoleRefId == r.Id))
                .OrderBy(r => r.RoleKey).Take(PerEntityLimit).ToListAsync(ct);
            return roles.Select(r => new AccessSearchRow(
                applicationId, "ROLE", r.Name, "No active assignments", "role", r.RoleKey)).ToList();
        }

        // Roles that grant no permissions.
        if (e == "role" && rel == "permissions")
        {
            List<RoleEntity> roles = await dbContext.Roles.AsNoTracking()
                .Where(r => r.ApplicationRefId == appRefId && r.Status == "ACTIVE"
                    && !dbContext.RolePermissions.Any(rp => rp.ApplicationRefId == appRefId && rp.State == "PUBLISHED" && rp.RoleRefId == r.Id))
                .OrderBy(r => r.RoleKey).Take(PerEntityLimit).ToListAsync(ct);
            return roles.Select(r => new AccessSearchRow(
                applicationId, "ROLE", r.Name, "Grants no permissions", "role", r.RoleKey)).ToList();
        }

        // Permissions granted by no role (orphan / unused permissions).
        if (e == "permission" && rel == "roles")
        {
            List<PermissionEntity> perms = await dbContext.Permissions.AsNoTracking()
                .Where(p => p.ApplicationRefId == appRefId
                    && !dbContext.RolePermissions.Any(rp => rp.ApplicationRefId == appRefId && rp.State == "PUBLISHED" && rp.PermissionRefId == p.Id))
                .OrderBy(p => p.PermissionKey).Take(PerEntityLimit).ToListAsync(ct);
            return perms.Select(p => new AccessSearchRow(
                applicationId, "PERMISSION", p.PermissionKey, "Granted by no role", "permission", p.PermissionKey)).ToList();
        }

        // Generic fallback: resolve children for exactly the capped parent keys and keep the parents
        // whose child count is zero. Bounded because parents are already capped at PerEntityLimit.
        List<AccessSearchRow> parents = await QueryEntityRowsAsync(appRefId, applicationId, scope, new AccessSearchSpec(entity, []), PerEntityLimit, ct);
        List<string> parentKeys = parents.Select(p => p.DeepLinkKey ?? p.Title).Distinct(StringComparer.Ordinal).ToList();
        IReadOnlyDictionary<string, List<AccessSearchRow>> childrenByKey =
            await Relationships.ResolveAsync(entity, relationship, dbContext, scope, parentKeys, int.MaxValue, ct);

        return parents
            .Where(p => !childrenByKey.TryGetValue(p.DeepLinkKey ?? p.Title, out List<AccessSearchRow>? kids) || kids.Count == 0)
            .Select(p => p with { Detail = $"No {relationship}" })
            .ToList();
    }

    // Relationship presence / compound absence (ASKAI-C3 per-application): base-entity rows that HAVE
    // at least one of presentRelationship and — when absentRelationship is also given — have ZERO of it.
    // Answers "roles that grant permissions but have no assignments", "permissions granted by a role but
    // with no policy". Restricted by Validate to application-partitioned entities, so each row belongs
    // to a single application and the per-application evaluation is correct.
    private async Task<List<AccessSearchRow>> QueryRelationshipPresenceAbsenceAsync(
        Guid appRefId, string applicationId, AccessScope scope, string entity, string presentRelationship, string? absentRelationship, CancellationToken ct)
    {
        List<AccessSearchRow> parents = await QueryEntityRowsAsync(appRefId, applicationId, scope, new AccessSearchSpec(entity, []), PerEntityLimit, ct);
        return await FilterByPresenceAbsenceAsync(scope, entity, parents, presentRelationship, absentRelationship, ct);
    }

    // Shared presence/absence filter (ASKAI-C3 per-application and ASKAI-C4 cross-application): keeps the
    // parents that HAVE at least one of presentRelationship (when given) and have ZERO of
    // absentRelationship (when given). Existence is decided by resolving each relationship for exactly
    // the given parent keys within the supplied scope (capped at one child per parent for cheapness,
    // bounded overall by the resolver FetchCap; see R-0015). The caller controls the scope, so the same
    // logic serves a single application (per-app) or the full accessible scope (platform single pass).
    private async Task<List<AccessSearchRow>> FilterByPresenceAbsenceAsync(
        AccessScope scope, string entity, List<AccessSearchRow> parents, string? presentRelationship, string? absentRelationship, CancellationToken ct)
    {
        List<string> parentKeys = parents.Select(p => p.DeepLinkKey ?? p.Title).Distinct(StringComparer.Ordinal).ToList();
        if (parentKeys.Count == 0)
        {
            return parents;
        }

        bool hasPresentRel = !string.IsNullOrWhiteSpace(presentRelationship);
        bool hasAbsentRel = !string.IsNullOrWhiteSpace(absentRelationship);

        // No present relationship → every parent passes the presence side; an absent-only query then
        // keeps the parents with zero of the absent relationship.
        HashSet<string> hasPresent = parentKeys.ToHashSet(StringComparer.Ordinal);
        if (hasPresentRel)
        {
            IReadOnlyDictionary<string, List<AccessSearchRow>> presentByKey =
                await Relationships.ResolveAsync(entity, presentRelationship!, dbContext, scope, parentKeys, 1, ct);
            hasPresent = presentByKey.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        }

        HashSet<string> hasAbsent = new(StringComparer.Ordinal);
        if (hasAbsentRel)
        {
            IReadOnlyDictionary<string, List<AccessSearchRow>> absentByKey =
                await Relationships.ResolveAsync(entity, absentRelationship!, dbContext, scope, parentKeys, 1, ct);
            hasAbsent = absentByKey.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        }

        string detail = hasPresentRel && hasAbsentRel ? $"Has {presentRelationship}, no {absentRelationship}"
            : hasPresentRel ? $"Has {presentRelationship}"
            : hasAbsentRel ? $"No {absentRelationship}"
            : string.Empty;

        return parents
            .Where(p =>
            {
                string key = p.DeepLinkKey ?? p.Title;
                return hasPresent.Contains(key) && (!hasAbsentRel || !hasAbsent.Contains(key));
            })
            .Select(p => p with { Detail = detail })
            .ToList();
    }

    private async Task<List<AccessSearchRow>> QueryRolesAsync(
        Guid appRefId, string applicationId, AccessSearchSpec spec, CancellationToken ct)
    {
        // Permission-related filters require the role's PUBLISHED grants.
        List<Guid> roleIdsByGrant = await RoleIdsMatchingGrantFiltersAsync(appRefId, spec, ct);
        bool hasGrantFilter = spec.Filters.Any(f => IsGrantField(f.Field));

        // The role's lifecycle status is now filterable. Only default to ACTIVE-only when the caller
        // didn't ask about status, so questions like "how many roles are inactive?" work.
        bool hasStatusFilter = spec.Filters.Any(f => f.Field.Equals("status", StringComparison.OrdinalIgnoreCase));
        IQueryable<RoleEntity> query = dbContext.Roles.AsNoTracking()
            .Where(r => r.ApplicationRefId == appRefId);
        if (!hasStatusFilter)
        {
            query = query.Where(r => r.Status == "ACTIVE");
        }

        foreach (AccessSearchFilter f in spec.Filters)
        {
            string value = (f.Value ?? string.Empty).ToLower();
            string op = f.Operator.ToLowerInvariant();
            switch (f.Field.ToLowerInvariant())
            {
                case "rolekey":
                    query = op == "contains"
                        ? query.Where(r => r.RoleKey.ToLower().Contains(value))
                        : query.Where(r => r.RoleKey.ToLower() == value);
                    break;
                case "name":
                    query = query.Where(r => r.Name.ToLower().Contains(value));
                    break;
                case "risklevel":
                {
                    string[] vals = SplitValues(f.Value);
                    query = op switch
                    {
                        "neq" => query.Where(r => r.RiskLevel.ToLower() != value),
                        "in" => query.Where(r => vals.Contains(r.RiskLevel.ToLower())),
                        _ => query.Where(r => r.RiskLevel.ToLower() == value),
                    };
                    break;
                }
                case "status":
                {
                    string[] vals = SplitValues(f.Value);
                    query = op switch
                    {
                        "neq" => query.Where(r => r.Status.ToLower() != value),
                        "in" => query.Where(r => vals.Contains(r.Status.ToLower())),
                        _ => query.Where(r => r.Status.ToLower() == value),
                    };
                    break;
                }
                case "privileged":
                    bool wantPrivileged = value is "true" or "yes" or "1";
                    query = query.Where(r => r.Privileged == wantPrivileged);
                    break;
            }
        }

        if (hasGrantFilter)
        {
            query = query.Where(r => roleIdsByGrant.Contains(r.Id));
        }

        List<RoleEntity> roles = await query.OrderBy(r => r.RoleKey).Take(PerEntityLimit).ToListAsync(ct);
        return roles.Select(r => new AccessSearchRow(
            applicationId,
            "ROLE",
            r.Name,
            $"{(r.Privileged ? "Privileged role" : "Role")} · {r.Status} · {r.RiskLevel} risk · {r.RoleKey}",
            "role",
            r.RoleKey)).ToList();
    }

    private async Task<List<AccessSearchRow>> QueryPermissionsAsync(
        Guid appRefId, string applicationId, AccessSearchSpec spec, CancellationToken ct)
    {
        IQueryable<PermissionEntity> query = dbContext.Permissions.AsNoTracking()
            .Where(p => p.ApplicationRefId == appRefId);
        bool hasStatusFilter = spec.Filters.Any(f => f.Field.Equals("status", StringComparison.OrdinalIgnoreCase));
        if (!hasStatusFilter)
        {
            query = query.Where(p => p.Status == "ACTIVE");
        }

        foreach (AccessSearchFilter f in spec.Filters)
        {
            string value = (f.Value ?? string.Empty).ToLower();
            string op = f.Operator.ToLowerInvariant();
            bool contains = op == "contains";
            switch (f.Field.ToLowerInvariant())
            {
                case "permissionkey":
                    query = contains ? query.Where(p => p.PermissionKey.ToLower().Contains(value))
                                     : query.Where(p => p.PermissionKey.ToLower() == value);
                    break;
                case "resource":
                    query = contains ? query.Where(p => p.Resource.ToLower().Contains(value))
                                     : query.Where(p => p.Resource.ToLower() == value);
                    break;
                case "action":
                    query = contains ? query.Where(p => p.Action.ToLower().Contains(value))
                                     : query.Where(p => p.Action.ToLower() == value);
                    break;
                case "risklevel":
                {
                    string[] vals = SplitValues(f.Value);
                    query = op switch
                    {
                        "neq" => query.Where(p => p.RiskLevel.ToLower() != value),
                        "in" => query.Where(p => vals.Contains(p.RiskLevel.ToLower())),
                        _ => query.Where(p => p.RiskLevel.ToLower() == value),
                    };
                    break;
                }
                case "status":
                {
                    string[] vals = SplitValues(f.Value);
                    query = op switch
                    {
                        "neq" => query.Where(p => p.Status.ToLower() != value),
                        "in" => query.Where(p => vals.Contains(p.Status.ToLower())),
                        _ => query.Where(p => p.Status.ToLower() == value),
                    };
                    break;
                }
            }
        }

        List<PermissionEntity> permissions = await query.OrderBy(p => p.PermissionKey).Take(PerEntityLimit).ToListAsync(ct);
        return permissions.Select(p => new AccessSearchRow(
            applicationId,
            "PERMISSION",
            p.PermissionKey,
            $"{p.Resource}:{p.Action} · {p.RiskLevel} risk · {p.Status}",
            "permission",
            p.PermissionKey)).ToList();
    }

    private async Task<List<AccessSearchRow>> QueryPoliciesAsync(
        Guid appRefId, string applicationId, AccessSearchSpec spec, CancellationToken ct)
    {
        var query =
            from policy in dbContext.Policies.AsNoTracking()
            join permission in dbContext.Permissions.AsNoTracking()
                on policy.PermissionRefId equals permission.Id into permJoin
            from permission in permJoin.DefaultIfEmpty()
            where policy.ApplicationRefId == appRefId
            select new { policy, permission };

        foreach (AccessSearchFilter f in spec.Filters)
        {
            string value = (f.Value ?? string.Empty).ToLower();
            bool contains = f.Operator.Equals("contains", StringComparison.OrdinalIgnoreCase);
            switch (f.Field.ToLowerInvariant())
            {
                case "policykey":
                    query = contains ? query.Where(x => x.policy.PolicyKey.ToLower().Contains(value))
                                     : query.Where(x => x.policy.PolicyKey.ToLower() == value);
                    break;
                case "effect":
                    query = query.Where(x => x.policy.Effect.ToLower() == value);
                    break;
                case "state":
                    query = query.Where(x => x.policy.State.ToLower() == value);
                    break;
                case "permission":
                    query = query.Where(x => x.permission != null && x.permission.PermissionKey.ToLower() == value);
                    break;
                case "resource":
                    query = query.Where(x => x.permission != null && x.permission.Resource.ToLower() == value);
                    break;
                case "action":
                    query = query.Where(x => x.permission != null && x.permission.Action.ToLower() == value);
                    break;
            }
        }

        var results = await query.OrderBy(x => x.policy.PolicyKey).Take(PerEntityLimit).ToListAsync(ct);
        return results.Select(x => new AccessSearchRow(
            applicationId,
            "POLICY",
            x.policy.PolicyKey,
            $"{x.policy.Effect} · {x.policy.State}" + (x.permission != null ? $" · {x.permission.PermissionKey}" : string.Empty),
            "policy",
            x.policy.PolicyKey)).ToList();
    }

    private async Task<List<AccessSearchRow>> QueryAssignmentsAsync(
        Guid appRefId, string applicationId, AccessSearchSpec spec, CancellationToken ct)
    {
        var query =
            from assignment in dbContext.Assignments.AsNoTracking()
            join role in dbContext.Roles.AsNoTracking()
                on assignment.RoleRefId equals role.Id into roleJoin
            from role in roleJoin.DefaultIfEmpty()
            where assignment.ApplicationRefId == appRefId
            select new { assignment, role };

        foreach (AccessSearchFilter f in spec.Filters)
        {
            string value = (f.Value ?? string.Empty).ToLower();
            string op = f.Operator.ToLowerInvariant();
            bool contains = op == "contains";
            switch (f.Field.ToLowerInvariant())
            {
                case "subject":
                    query = contains
                        ? query.Where(x => x.assignment.SubjectEmail != null && x.assignment.SubjectEmail.ToLower().Contains(value))
                        : query.Where(x => x.assignment.SubjectEmail != null && x.assignment.SubjectEmail.ToLower() == value);
                    break;
                case "role":
                    query = query.Where(x => x.role != null && x.role.RoleKey.ToLower() == value);
                    break;
                case "resourcetype":
                    query = query.Where(x => x.assignment.ResourceType != null && x.assignment.ResourceType.ToLower() == value);
                    break;
                case "resourceid":
                    query = contains
                        ? query.Where(x => x.assignment.ResourceId != null && x.assignment.ResourceId.ToLower().Contains(value))
                        : query.Where(x => x.assignment.ResourceId != null && x.assignment.ResourceId.ToLower() == value);
                    break;
                case "state":
                {
                    string[] vals = SplitValues(f.Value);
                    query = op switch
                    {
                        "neq" => query.Where(x => x.assignment.State.ToLower() != value),
                        "in" => query.Where(x => vals.Contains(x.assignment.State.ToLower())),
                        _ => query.Where(x => x.assignment.State.ToLower() == value),
                    };
                    break;
                }
                case "source":
                    query = query.Where(x => x.assignment.Source.ToLower() == value);
                    break;
                case "validuntil":
                {
                    // Answers expiry questions, e.g. "past expiry" → validUntil lt now.
                    DateTimeOffset? instant = ParseInstant(f.Value);
                    if (instant is DateTimeOffset t)
                    {
                        query = op switch
                        {
                            "gt" => query.Where(x => x.assignment.ValidUntil != null && x.assignment.ValidUntil > t),
                            "gte" => query.Where(x => x.assignment.ValidUntil != null && x.assignment.ValidUntil >= t),
                            "lte" => query.Where(x => x.assignment.ValidUntil != null && x.assignment.ValidUntil <= t),
                            _ => query.Where(x => x.assignment.ValidUntil != null && x.assignment.ValidUntil < t),
                        };
                    }

                    break;
                }
            }
        }

        var results = await query
            .OrderBy(x => x.assignment.SubjectEmail)
            .Take(PerEntityLimit)
            .ToListAsync(ct);

        return results.Select(x => new AccessSearchRow(
            applicationId,
            "ASSIGNMENT",
            x.assignment.SubjectEmail ?? x.assignment.GroupId ?? "(unknown subject)",
            $"{(x.role != null ? x.role.RoleKey : "?")} · {x.assignment.State}"
                + (x.assignment.ValidUntil != null ? $" · until {x.assignment.ValidUntil.Value.UtcDateTime:yyyy-MM-dd}" : string.Empty)
                + (x.assignment.ResourceId != null ? $" · {x.assignment.ResourceId}" : string.Empty),
            x.assignment.SubjectEmail != null ? "user" : null,
            x.assignment.SubjectEmail)).ToList();
    }

    private async Task<List<AccessSearchRow>> QuerySubjectsAsync(
        Guid appRefId, string applicationId, AccessSearchSpec spec, CancellationToken ct)
    {
        List<Guid> roleIdsByGrant = await RoleIdsMatchingGrantFiltersAsync(appRefId, spec, ct);
        bool hasGrantFilter = spec.Filters.Any(f => IsGrantField(f.Field));

        var query =
            from assignment in dbContext.Assignments.AsNoTracking()
            join role in dbContext.Roles.AsNoTracking()
                on assignment.RoleRefId equals role.Id into roleJoin
            from role in roleJoin.DefaultIfEmpty()
            where assignment.ApplicationRefId == appRefId
                && assignment.State == "ACTIVE"
                && assignment.SubjectEmail != null
            select new { assignment, role };

        foreach (AccessSearchFilter f in spec.Filters)
        {
            string value = (f.Value ?? string.Empty).ToLower();
            bool contains = f.Operator.Equals("contains", StringComparison.OrdinalIgnoreCase);
            switch (f.Field.ToLowerInvariant())
            {
                case "subject":
                    query = contains
                        ? query.Where(x => x.assignment.SubjectEmail!.ToLower().Contains(value))
                        : query.Where(x => x.assignment.SubjectEmail!.ToLower() == value);
                    break;
                case "role":
                    query = query.Where(x => x.role != null && x.role.RoleKey.ToLower() == value);
                    break;
                case "resourceid":
                    query = query.Where(x => x.assignment.ResourceId != null && x.assignment.ResourceId.ToLower().Contains(value));
                    break;
            }
        }

        if (hasGrantFilter)
        {
            query = query.Where(x => roleIdsByGrant.Contains(x.assignment.RoleRefId));
        }

        var results = await query.Take(PerEntityLimit * 4).ToListAsync(ct);

        return results
            .Where(x => x.assignment.SubjectEmail is not null)
            .GroupBy(x => x.assignment.SubjectEmail!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(PerEntityLimit)
            .Select(g => new AccessSearchRow(
                applicationId,
                "SUBJECT",
                g.Key,
                "via " + string.Join(", ", g.Where(x => x.role != null)
                    .Select(x => x.role!.RoleKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)),
                "user",
                g.Key))
            .ToList();
    }

    private static bool IsGrantField(string field) =>
        field.Equals("permission", StringComparison.OrdinalIgnoreCase)
        || field.Equals("resource", StringComparison.OrdinalIgnoreCase)
        || field.Equals("action", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve the ids of roles whose PUBLISHED permission grants match the permission/resource/action
    /// filters in the spec. Used by the role and subject entities to answer "who/what can do X".
    /// </summary>
    private async Task<List<Guid>> RoleIdsMatchingGrantFiltersAsync(
        Guid appRefId, AccessSearchSpec spec, CancellationToken ct)
    {
        List<AccessSearchFilter> grantFilters = spec.Filters.Where(f => IsGrantField(f.Field)).ToList();
        if (grantFilters.Count == 0)
        {
            return [];
        }

        var query =
            from rp in dbContext.RolePermissions.AsNoTracking()
            join permission in dbContext.Permissions.AsNoTracking()
                on rp.PermissionRefId equals permission.Id
            where rp.ApplicationRefId == appRefId && rp.State == "PUBLISHED"
            select new { rp.RoleRefId, permission };

        foreach (AccessSearchFilter f in grantFilters)
        {
            string value = (f.Value ?? string.Empty).ToLower();
            switch (f.Field.ToLowerInvariant())
            {
                case "permission":
                    query = query.Where(x => x.permission.PermissionKey.ToLower() == value);
                    break;
                case "resource":
                    query = query.Where(x => x.permission.Resource.ToLower() == value);
                    break;
                case "action":
                    query = query.Where(x => x.permission.Action.ToLower() == value);
                    break;
            }
        }

        return await query.Select(x => x.RoleRefId).Distinct().ToListAsync(ct);
    }
}

/// <summary>A validated access-search query spec in the closed F8 schema.</summary>
public sealed record AccessSearchSpec(
    string Entity,
    IReadOnlyList<AccessSearchFilter> Filters,
    string? Aggregate = null,
    string? GroupByRelationship = null,
    string? Include = null,
    string? GroupByField = null,
    int? Limit = null,
    string? AbsentRelationship = null,
    string? IncludeChild = null,
    string? PresentRelationship = null,
    string? GroupBySecondaryField = null,
    string? IncludeGrandchild = null);

/// <summary>A single filter over a known field with a bounded operator (<c>eq</c> or <c>contains</c>).</summary>
public sealed record AccessSearchFilter(string Field, string Operator, string Value);

/// <summary>
/// The result of resolving a spec's filter values against live data. On success carries the spec
/// with canonicalised values; on failure carries a user-facing reason for a guidance response.
/// </summary>
public sealed record AccessSearchValueResolution(AccessSearchSpec? Spec, string? Error)
{
    public bool Success => Error is null;

    public static AccessSearchValueResolution Ok(AccessSearchSpec spec) => new(spec, null);

    public static AccessSearchValueResolution Fail(string error) => new(null, error);
}

/// <summary>A citable result row with enough context to deep-link into the portal.</summary>
public sealed record AccessSearchRow(
    string ApplicationId,
    string EntityType,
    string Title,
    string Detail,
    string? DeepLinkKind,
    string? DeepLinkKey,
    IReadOnlyList<AccessSearchRow>? Children = null,
    string ApplicationName = "",
    string TenantName = "");

/// <summary>The result of validating and (if valid) executing a spec.</summary>
public sealed record AccessSearchOutcome(
    bool IsValid,
    string? Error,
    string Entity,
    IReadOnlyList<AccessSearchRow> Rows,
    string Mode = AccessSearchModes.Records);

/// <summary>How a result set should be presented: flat records, a scalar count, per-group counts, or a parent→children tree.</summary>
public static class AccessSearchModes
{
    public const string Records = "records";
    public const string Count = "count";
    public const string Group = "group";
    public const string Tree = "tree";

    /// <summary>The question could not be answered; the response instead carries helpful guidance and suggestions.</summary>
    public const string Guidance = "guidance";
}
