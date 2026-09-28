using System.Linq.Expressions;
using System.Reflection;
using Authorization.Ai;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Ai;

/// <summary>
/// The application scope a generic access-search query runs within. Every registered entity query
/// is bounded to these ids so the search can never cross the caller's accessible-application
/// boundary. Populated per call from the resolved application(s).
/// </summary>
public sealed record AccessScope(IReadOnlyList<Guid> AppRefIds, IReadOnlyList<string> AppIds, bool IncludePlatformScoped = false);

/// <summary>
/// One entity the generic (registry-driven) part of F8 can answer about. Implementations expose a
/// closed, reflection-derived field allow-list and run only scoped, parameterized, read-only
/// queries — the AI layer never authors SQL and any unknown field/operator is rejected here.
/// </summary>
public interface IRegisteredAccessEntity
{
    string Name { get; }

    IReadOnlyList<string> Fields { get; }

    /// <summary>Returns a validation error for an unknown field/operator, or <c>null</c> when valid.</summary>
    string? Validate(AccessSearchSpec spec);

    Task<List<AccessSearchRow>> QueryAsync(
        AuthorizationDbContext dbContext,
        AccessScope scope,
        AccessSearchSpec spec,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Accurate count of the rows matching the spec's filters, within scope.</summary>
    Task<int> CountAsync(
        AuthorizationDbContext dbContext,
        AccessScope scope,
        AccessSearchSpec spec,
        CancellationToken cancellationToken);
}

/// <summary>
/// Generic, closed, read-only descriptor for an entity type <typeparamref name="T"/>. The filterable
/// field allow-list is derived from the entity's scalar columns via reflection ("all columns"); each
/// filter is turned into a parameterized EF predicate through an expression tree, mirroring the
/// hand-written executor's <c>ToLower()/Contains()</c> patterns so it translates on both EF InMemory
/// (tests) and Npgsql (runtime). A spec that references an unknown field or operator is rejected.
/// </summary>
public sealed class RegisteredAccessEntity<T> : IRegisteredAccessEntity
    where T : class
{
    private readonly Func<AuthorizationDbContext, AccessScope, IQueryable<T>> baseQuery;
    private readonly Func<T, AccessSearchRow> project;
    private readonly IReadOnlyDictionary<string, PropertyInfo> fieldProperties;

    public RegisteredAccessEntity(
        string name,
        Func<AuthorizationDbContext, AccessScope, IQueryable<T>> baseQuery,
        Func<T, AccessSearchRow> project)
    {
        Name = name;
        this.baseQuery = baseQuery;
        this.project = project;
        fieldProperties = DiscoverFields();
        Fields = fieldProperties.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    public string Name { get; }

    public IReadOnlyList<string> Fields { get; }

    public string? Validate(AccessSearchSpec spec)
    {
        foreach (AccessSearchFilter filter in spec.Filters)
        {
            if (!fieldProperties.ContainsKey(filter.Field))
            {
                return $"Unknown field '{filter.Field}' for entity '{Name}'. Allowed: {string.Join(", ", Fields)}.";
            }

            if (!IsKnownOperator(filter.Operator))
            {
                return $"Unknown operator '{filter.Operator}'. Allowed: eq, neq, contains, in, gt, gte, lt, lte.";
            }
        }

        return null;
    }

    public async Task<List<AccessSearchRow>> QueryAsync(
        AuthorizationDbContext dbContext,
        AccessScope scope,
        AccessSearchSpec spec,
        int limit,
        CancellationToken cancellationToken)
    {
        IQueryable<T>? query = BuildFilteredQuery(dbContext, scope, spec);
        if (query is null)
        {
            return [];
        }

        List<T> items = await query.Take(limit).ToListAsync(cancellationToken);
        return items.Select(project).ToList();
    }

    public async Task<int> CountAsync(
        AuthorizationDbContext dbContext,
        AccessScope scope,
        AccessSearchSpec spec,
        CancellationToken cancellationToken)
    {
        IQueryable<T>? query = BuildFilteredQuery(dbContext, scope, spec);
        return query is null ? 0 : await query.CountAsync(cancellationToken);
    }

    /// <summary>
    /// Builds the scoped, filtered query shared by <see cref="QueryAsync"/> and
    /// <see cref="CountAsync"/>. Returns <c>null</c> when a filter value cannot be parsed for its
    /// column type (e.g. non-Guid text for a Guid column) — an unmatchable spec, treated as no rows.
    /// </summary>
    private IQueryable<T>? BuildFilteredQuery(AuthorizationDbContext dbContext, AccessScope scope, AccessSearchSpec spec)
    {
        IQueryable<T> query = baseQuery(dbContext, scope).AsNoTracking();

        foreach (AccessSearchFilter filter in spec.Filters)
        {
            PropertyInfo property = fieldProperties[filter.Field];
            Expression<Func<T, bool>>? predicate = BuildPredicate(property, filter.Operator, filter.Value ?? string.Empty);
            if (predicate is null)
            {
                return null;
            }

            query = query.Where(predicate);
        }

        return query;
    }

    private static bool IsKnownOperator(string op) =>
        op.Equals("eq", StringComparison.OrdinalIgnoreCase)
        || op.Equals("neq", StringComparison.OrdinalIgnoreCase)
        || op.Equals("contains", StringComparison.OrdinalIgnoreCase)
        || op.Equals("in", StringComparison.OrdinalIgnoreCase)
        || op.Equals("gt", StringComparison.OrdinalIgnoreCase)
        || op.Equals("gte", StringComparison.OrdinalIgnoreCase)
        || op.Equals("lt", StringComparison.OrdinalIgnoreCase)
        || op.Equals("lte", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, PropertyInfo> DiscoverFields()
    {
        var fields = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (PropertyInfo property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !IsFilterable(property.PropertyType))
            {
                continue;
            }

            fields[ToCamelCase(property.Name)] = property;
        }

        return fields;
    }

    private static bool IsFilterable(Type type)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(string)
            || underlying == typeof(bool)
            || underlying == typeof(Guid)
            || underlying == typeof(int)
            || underlying == typeof(long)
            || underlying == typeof(DateTimeOffset)
            || type == typeof(string[]);
    }

    private static Expression<Func<T, bool>>? BuildPredicate(PropertyInfo property, string op, string rawValue)
    {
        ParameterExpression parameter = Expression.Parameter(typeof(T), "e");
        Expression member = Expression.Property(parameter, property);
        Type type = property.PropertyType;
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;
        string opLower = op.ToLowerInvariant();
        bool contains = opLower == "contains";

        if (type == typeof(string[]))
        {
            // Array membership: "does this string[] column contain the value". Npgsql translates
            // Enumerable.Contains on an array column to `= ANY`; EF InMemory runs it in memory.
            MethodInfo containsMethod = typeof(Enumerable).GetMethods()
                .First(m => m.Name == "Contains" && m.GetParameters().Length == 2)
                .MakeGenericMethod(typeof(string));
            Expression arrayContains = Expression.Call(containsMethod, member, Expression.Constant(rawValue));
            Expression notNullArray = Expression.NotEqual(member, Expression.Constant(null, type));
            return Expression.Lambda<Func<T, bool>>(Expression.AndAlso(notNullArray, arrayContains), parameter);
        }

        if (underlying == typeof(string))
        {
            MethodInfo toLower = typeof(string).GetMethod("ToLower", Type.EmptyTypes)!;
            Expression lowered = Expression.Call(member, toLower);
            Expression notNull = Expression.NotEqual(member, Expression.Constant(null, typeof(string)));

            // Set membership: "disabled,deprecated" → the column's lowered value is one of the list.
            if (opLower == "in")
            {
                List<string> values = rawValue.ToLowerInvariant()
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                MethodInfo listContains = typeof(List<string>).GetMethod("Contains", [typeof(string)])!;
                Expression inCall = Expression.Call(Expression.Constant(values), listContains, lowered);
                return Expression.Lambda<Func<T, bool>>(Expression.AndAlso(notNull, inCall), parameter);
            }

            string value = rawValue.ToLowerInvariant();
            Expression comparison = opLower switch
            {
                "contains" => Expression.Call(lowered, typeof(string).GetMethod("Contains", [typeof(string)])!, Expression.Constant(value)),
                "neq" => Expression.NotEqual(lowered, Expression.Constant(value)),
                _ => Expression.Equal(lowered, Expression.Constant(value)),
            };
            return Expression.Lambda<Func<T, bool>>(Expression.AndAlso(notNull, comparison), parameter);
        }

        // Scalars: equality/inequality for all, plus ordered comparisons for numeric/date columns.
        object? parsed = ParseScalar(underlying, rawValue);
        if (parsed is null)
        {
            return null;
        }

        Expression constant = Expression.Constant(parsed, underlying);
        Expression left = underlying == type ? member : Expression.Convert(member, underlying);
        bool isOrdered = underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(DateTimeOffset);
        Expression body = opLower switch
        {
            "neq" => Expression.NotEqual(left, constant),
            "gt" when isOrdered => Expression.GreaterThan(left, constant),
            "gte" when isOrdered => Expression.GreaterThanOrEqual(left, constant),
            "lt" when isOrdered => Expression.LessThan(left, constant),
            "lte" when isOrdered => Expression.LessThanOrEqual(left, constant),
            _ => Expression.Equal(left, constant),
        };

        // For nullable columns, guard the null before comparing so a null row is simply excluded.
        if (Nullable.GetUnderlyingType(type) is not null)
        {
            Expression hasValue = Expression.NotEqual(member, Expression.Constant(null, type));
            return Expression.Lambda<Func<T, bool>>(Expression.AndAlso(hasValue, body), parameter);
        }

        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    private static object? ParseScalar(Type underlying, string rawValue)
    {
        if (underlying == typeof(bool))
        {
            string v = rawValue.Trim().ToLowerInvariant();
            return v is "true" or "yes" or "1" ? true
                : v is "false" or "no" or "0" ? false
                : null;
        }

        if (underlying == typeof(Guid))
        {
            return Guid.TryParse(rawValue, out Guid g) ? g : null;
        }

        if (underlying == typeof(int))
        {
            return int.TryParse(rawValue, out int i) ? i : null;
        }

        if (underlying == typeof(long))
        {
            return long.TryParse(rawValue, out long l) ? l : null;
        }

        if (underlying == typeof(DateTimeOffset))
        {
            string v = rawValue.Trim();
            if (v.Equals("now", StringComparison.OrdinalIgnoreCase) || v.Equals("today", StringComparison.OrdinalIgnoreCase))
            {
                return DateTimeOffset.UtcNow;
            }

            return DateTimeOffset.TryParse(rawValue, out DateTimeOffset d) ? d : null;
        }

        return null;
    }

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) || char.IsLower(name[0])
            ? name
            : char.ToLowerInvariant(name[0]) + name[1..];
}

/// <summary>
/// The registry of additional governance entities the AI access search can answer about beyond the
/// five hand-written entities (role, permission, policy, assignment, subject). Each entry is a
/// closed, scoped, read-only descriptor; the model plans against the merged schema and this registry
/// re-validates and executes. Credential/secret columns are never registered.
/// </summary>
public sealed class AccessSearchRegistry
{
    private readonly IReadOnlyDictionary<string, IRegisteredAccessEntity> entities;

    public AccessSearchRegistry()
    {
        var list = new List<IRegisteredAccessEntity>
        {
            new RegisteredAccessEntity<SodRuleEntity>(
                "sodRule",
                (db, scope) => db.SodRules.Where(e => scope.AppRefIds.Contains(e.ApplicationRefId)),
                e => new AccessSearchRow(string.Empty, "SOD_RULE", e.Name, $"{e.Severity} · {e.Status} · {e.RuleKey}", null, null)),

            new RegisteredAccessEntity<ReferenceDataEntity>(
                "referenceData",
                (db, scope) => db.ReferenceData.Where(e => scope.AppRefIds.Contains(e.ApplicationRefId)),
                e => new AccessSearchRow(string.Empty, "REFERENCE_DATA", e.Key, e.Status, null, null)),

            new RegisteredAccessEntity<ApplicationEntity>(
                "application",
                (db, scope) => db.Applications.Where(e => scope.AppIds.Contains(e.ApplicationId)),
                e => new AccessSearchRow(e.ApplicationId, "APPLICATION", e.Name, $"{e.RiskLevel} risk · {e.Status}", "application", e.ApplicationId)),

            new RegisteredAccessEntity<OidcProviderEntity>(
                "oidcProvider",
                (db, scope) => db.OidcProviders.Where(e => scope.AppRefIds.Contains(e.ApplicationRefId)),
                e => new AccessSearchRow(string.Empty, "OIDC_PROVIDER", e.Issuer, $"{e.ProviderType} · {(e.Enabled ? "enabled" : "disabled")}", null, null)),

            new RegisteredAccessEntity<AssignmentAttributeEntity>(
                "assignmentAttribute",
                (db, scope) => db.AssignmentAttributes.Where(e =>
                    db.Assignments.Where(a => scope.AppRefIds.Contains(a.ApplicationRefId)).Select(a => a.Id).Contains(e.AssignmentId)),
                e => new AccessSearchRow(string.Empty, "ASSIGNMENT_ATTRIBUTE", e.Name, e.ValueType, null, null)),

            new RegisteredAccessEntity<AuditEventEntity>(
                "auditEvent",
                (db, scope) => db.AuditEvents.Where(e => e.ApplicationId != null && scope.AppIds.Contains(e.ApplicationId)),
                e => new AccessSearchRow(e.ApplicationId ?? string.Empty, "AUDIT_EVENT", e.EventType, $"{e.ActorEmail ?? "system"} · {e.Timestamp:u}", null, null)),

            new RegisteredAccessEntity<DecisionEntity>(
                "decision",
                (db, scope) => db.Decisions.Where(e => scope.AppIds.Contains(e.ApplicationId)),
                e => new AccessSearchRow(e.ApplicationId, "DECISION", $"{e.Action} on {e.ResourceType}", e.Allowed ? "ALLOW" : $"DENY · {e.DenyReason}", null, null)),

            // AI invocations/prompt logs are recorded with a null ApplicationId for platform-wide
            // features (e.g. Ask AI itself), and a real ApplicationId only for app-scoped AI helpers.
            // Platform-scoped (null) rows are included only when the caller can see the whole platform
            // (scope.IncludePlatformScoped) — mirrors PlatformAiAssistController.GetPromptLogsAsync.
            new RegisteredAccessEntity<AiInvocationEntity>(
                "aiInvocation",
                (db, scope) => db.AiInvocations.Where(e =>
                    (e.ApplicationId != null && scope.AppIds.Contains(e.ApplicationId))
                    || (e.ApplicationId == null && scope.IncludePlatformScoped)),
                e => new AccessSearchRow(e.ApplicationId ?? string.Empty, "AI_INVOCATION", e.Feature, $"{e.Outcome} · {e.Provider}", null, null)),

            new RegisteredAccessEntity<AiPromptLogEntity>(
                "aiPromptLog",
                (db, scope) => db.AiPromptLogs.Where(e =>
                    (e.ApplicationId != null && scope.AppIds.Contains(e.ApplicationId))
                    || (e.ApplicationId == null && scope.IncludePlatformScoped)),
                // Title is the actual question asked (prompt_text), not the feature name — every
                // aiPromptLog row currently shares the same feature ("accessSearch"), so showing that
                // as the title would make every distinct failed/succeeded prompt look identical.
                e => new AccessSearchRow(e.ApplicationId ?? string.Empty, "AI_PROMPT_LOG", e.PromptText, e.Outcome, null, null)),

            new RegisteredAccessEntity<TenantEntity>(
                "tenant",
                // A tenant is in scope when it owns at least one accessible application. Cross-app by
                // nature; the platform controller de-duplicates identical rows across its per-app loop.
                (db, scope) => db.Tenants.Where(t =>
                    db.Applications.Where(a => scope.AppIds.Contains(a.ApplicationId)).Select(a => a.TenantRefId).Contains(t.Id)),
                e => new AccessSearchRow(string.Empty, "TENANT", e.Name, $"{e.Status} · {e.TenantId}", "tenant", e.TenantId)),

            new RegisteredAccessEntity<RolePermissionEntity>(
                "rolePermission",
                (db, scope) => db.RolePermissions.Where(e => scope.AppRefIds.Contains(e.ApplicationRefId)),
                e => new AccessSearchRow(string.Empty, "ROLE_PERMISSION", $"grant ({e.State})", $"role {e.RoleRefId} → permission {e.PermissionRefId}", null, null)),

            new RegisteredAccessEntity<ReviewCampaignEntity>(
                "reviewCampaign",
                (db, scope) => db.ReviewCampaigns.Where(e => scope.AppRefIds.Contains(e.ApplicationRefId)),
                // No per-campaign route exists yet (Certifications is a flat list with client-side
                // selection), so the deep link opens that app's Certifications page where the named
                // campaign can be found. deepLinkKey is unused by the route but must be non-empty for
                // the frontend to render the "Open" button.
                e => new AccessSearchRow(string.Empty, "REVIEW_CAMPAIGN", e.Name, e.Status, "reviewCampaign", e.Id.ToString())),
        };

        entities = list.ToDictionary(e => e.Name, e => e, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> Names => (IReadOnlyCollection<string>)entities.Keys;

    public IEnumerable<AccessSearchEntitySchema> Schema =>
        entities.Values.Select(e => new AccessSearchEntitySchema(e.Name, e.Fields));

    public bool TryGet(string entity, out IRegisteredAccessEntity descriptor) =>
        entities.TryGetValue(entity, out descriptor!);
}
