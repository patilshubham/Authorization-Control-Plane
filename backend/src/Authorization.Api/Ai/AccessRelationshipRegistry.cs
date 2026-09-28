using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Ai;

/// <summary>
/// The closed set of parent → child relationships the access search can traverse (Phase 3 "include"
/// and the generic form of Phase 4 relationship counts). Each resolver runs a read-only, scoped,
/// parameterized query keyed by the parent's <b>natural key</b> (role key, permission key,
/// application id, tenant id, …) and returns citable child rows grouped by that parent. The model
/// only ever names a registered relationship; an unknown one is rejected before execution.
/// </summary>
public sealed class AccessRelationshipRegistry
{
    // Upper bound on rows pulled per relationship query before grouping/capping — keeps a broad
    // include bounded even if a parent has many children.
    private const int FetchCap = 2000;

    /// <summary>Resolves child (parentKey, row) pairs for a set of parent natural keys, within scope.</summary>
    public delegate Task<List<(string ParentKey, AccessSearchRow Child)>> Resolver(
        AuthorizationDbContext db,
        AccessScope scope,
        IReadOnlyList<string> parentKeys,
        CancellationToken cancellationToken);

    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, Resolver>> byEntity;

    public AccessRelationshipRegistry()
    {
        byEntity = Build();
    }

    /// <summary>The relationship names available on an entity (for the planner schema).</summary>
    public IReadOnlyList<string> RelationshipsFor(string entity) =>
        byEntity.TryGetValue(entity, out IReadOnlyDictionary<string, Resolver>? rels)
            ? rels.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList()
            : [];

    public bool Has(string entity, string relationship) =>
        byEntity.TryGetValue(entity, out IReadOnlyDictionary<string, Resolver>? rels)
        && rels.ContainsKey(relationship);

    // The child entity each relationship resolves to. Mirrors Build() and the child rows' DeepLinkKind,
    // and drives a bounded second-level "include" (ASKAI-C2): the child entity's own relationships are
    // the only valid next hop. Relationships whose child is a leaf (assignment/oidcProvider/sodRule/
    // referenceData) map to a name with no registered relationships, so a second hop off them is
    // rejected as unknown — exactly the closed-spec behaviour we want.
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> TargetEntities =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["role"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["permissions"] = "permission", ["assignments"] = "assignment", ["policies"] = "policy" },
            ["permission"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["roles"] = "role", ["policies"] = "policy" },
            ["policy"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["permissions"] = "permission", ["roles"] = "role" },
            ["subject"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["roles"] = "role", ["permissions"] = "permission", ["applications"] = "application" },
            ["application"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["roles"] = "role", ["permissions"] = "permission", ["policies"] = "policy",
                ["assignments"] = "assignment", ["subjects"] = "subject", ["oidcProviders"] = "oidcProvider",
                ["sodRules"] = "sodRule", ["referenceData"] = "referenceData",
                ["unusedPermissions"] = "permission", ["unassignedRoles"] = "role", ["tenant"] = "tenant",
            },
            ["tenant"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["applications"] = "application", ["roles"] = "role", ["permissions"] = "permission", ["policies"] = "policy", ["assignments"] = "assignment" },
        };

    /// <summary>The entity a relationship resolves to, or null when the relationship is unknown.</summary>
    public string? TargetEntityFor(string entity, string relationship) =>
        TargetEntities.TryGetValue(entity, out IReadOnlyDictionary<string, string>? rels)
        && rels.TryGetValue(relationship, out string? target)
            ? target
            : null;

    /// <summary>
    /// Resolve children grouped by parent natural key, capped at <paramref name="perParentLimit"/>
    /// children per parent. Returns an empty map for an unknown relationship or no parents.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, List<AccessSearchRow>>> ResolveAsync(
        string entity,
        string relationship,
        AuthorizationDbContext db,
        AccessScope scope,
        IReadOnlyList<string> parentKeys,
        int perParentLimit,
        CancellationToken cancellationToken)
    {
        var grouped = new Dictionary<string, List<AccessSearchRow>>(StringComparer.Ordinal);
        if (parentKeys.Count == 0
            || !byEntity.TryGetValue(entity, out IReadOnlyDictionary<string, Resolver>? rels)
            || !rels.TryGetValue(relationship, out Resolver? resolver))
        {
            return grouped;
        }

        List<(string ParentKey, AccessSearchRow Child)> pairs = await resolver(db, scope, parentKeys, cancellationToken);
        foreach ((string parentKey, AccessSearchRow child) in pairs)
        {
            if (!grouped.TryGetValue(parentKey, out List<AccessSearchRow>? list))
            {
                list = [];
                grouped[parentKey] = list;
            }

            if (list.Count < perParentLimit)
            {
                list.Add(child);
            }
        }

        return grouped;
    }

    private static AccessSearchRow Child(string type, string title, string detail, string? kind, string? key) =>
        new(string.Empty, type, title, detail, kind, key);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, Resolver>> Build()
    {
        var map = new Dictionary<string, IReadOnlyDictionary<string, Resolver>>(StringComparer.OrdinalIgnoreCase)
        {
            ["role"] = new Dictionary<string, Resolver>(StringComparer.OrdinalIgnoreCase)
            {
                ["permissions"] = RolePermissions,
                ["assignments"] = RoleAssignments,
                ["policies"] = RolePolicies,
            },
            ["permission"] = new Dictionary<string, Resolver>(StringComparer.OrdinalIgnoreCase)
            {
                ["roles"] = PermissionRoles,
                ["policies"] = PermissionPolicies,
            },
            ["policy"] = new Dictionary<string, Resolver>(StringComparer.OrdinalIgnoreCase)
            {
                ["permissions"] = PolicyPermission,
                ["roles"] = PolicyRoles,
            },
            ["subject"] = new Dictionary<string, Resolver>(StringComparer.OrdinalIgnoreCase)
            {
                ["roles"] = SubjectRoles,
                ["permissions"] = SubjectPermissions,
                ["applications"] = SubjectApplications,
            },
            ["application"] = new Dictionary<string, Resolver>(StringComparer.OrdinalIgnoreCase)
            {
                ["roles"] = ApplicationRoles,
                ["permissions"] = ApplicationPermissions,
                ["policies"] = ApplicationPolicies,
                ["assignments"] = ApplicationAssignments,
                ["subjects"] = ApplicationSubjects,
                ["oidcProviders"] = ApplicationOidcProviders,
                ["sodRules"] = ApplicationSodRules,
                ["referenceData"] = ApplicationReferenceData,
                ["unusedPermissions"] = ApplicationUnusedPermissions,
                ["unassignedRoles"] = ApplicationUnassignedRoles,
                ["tenant"] = ApplicationTenant,
            },
            ["tenant"] = new Dictionary<string, Resolver>(StringComparer.OrdinalIgnoreCase)
            {
                ["applications"] = TenantApplications,
                ["roles"] = TenantRoles,
                ["permissions"] = TenantPermissions,
                ["policies"] = TenantPolicies,
                ["assignments"] = TenantAssignments,
            },
        };

        return map;
    }

    // ── Built-in entity relationships (keyed by natural key) ────────────────────────────────────

    private static async Task<List<(string, AccessSearchRow)>> RolePermissions(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from rp in db.RolePermissions.AsNoTracking()
            where scope.AppRefIds.Contains(rp.ApplicationRefId) && rp.State == "PUBLISHED"
            join role in db.Roles.AsNoTracking() on rp.RoleRefId equals role.Id
            where role.Status == "ACTIVE" && keys.Contains(role.RoleKey)
            join perm in db.Permissions.AsNoTracking() on rp.PermissionRefId equals perm.Id
            orderby perm.PermissionKey
            select new { role.RoleKey, perm.PermissionKey, perm.Resource, perm.Action, perm.RiskLevel })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.RoleKey,
            Child("PERMISSION", x.PermissionKey, $"{x.Resource}:{x.Action} · {x.RiskLevel} risk", "permission", x.PermissionKey))).ToList();
    }

    private static async Task<List<(string, AccessSearchRow)>> RoleAssignments(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from a in db.Assignments.AsNoTracking()
            where scope.AppRefIds.Contains(a.ApplicationRefId) && a.State == "ACTIVE"
            join role in db.Roles.AsNoTracking() on a.RoleRefId equals role.Id
            where keys.Contains(role.RoleKey)
            select new { role.RoleKey, a.SubjectEmail, a.GroupId, a.State })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.RoleKey,
            Child("ASSIGNMENT", x.SubjectEmail ?? x.GroupId ?? "(subject)", x.State, x.SubjectEmail != null ? "user" : null, x.SubjectEmail))).ToList();
    }

    private static async Task<List<(string, AccessSearchRow)>> PermissionRoles(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from rp in db.RolePermissions.AsNoTracking()
            where scope.AppRefIds.Contains(rp.ApplicationRefId) && rp.State == "PUBLISHED"
            join perm in db.Permissions.AsNoTracking() on rp.PermissionRefId equals perm.Id
            where keys.Contains(perm.PermissionKey)
            join role in db.Roles.AsNoTracking() on rp.RoleRefId equals role.Id
            where role.Status == "ACTIVE"
            orderby role.RoleKey
            select new { perm.PermissionKey, role.RoleKey, role.Privileged, role.RiskLevel })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.PermissionKey,
            Child("ROLE", x.RoleKey, $"{(x.Privileged ? "Privileged" : "Role")} · {x.RiskLevel} risk", "role", x.RoleKey))).ToList();
    }

    private static async Task<List<(string, AccessSearchRow)>> PermissionPolicies(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from p in db.Policies.AsNoTracking()
            where scope.AppRefIds.Contains(p.ApplicationRefId)
            join perm in db.Permissions.AsNoTracking() on p.PermissionRefId equals perm.Id
            where keys.Contains(perm.PermissionKey)
            orderby p.PolicyKey
            select new { perm.PermissionKey, p.PolicyKey, p.Effect, p.State })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.PermissionKey,
            Child("POLICY", x.PolicyKey, $"{x.Effect} · {x.State}", "policy", x.PolicyKey))).ToList();
    }

    private static async Task<List<(string, AccessSearchRow)>> PolicyPermission(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from p in db.Policies.AsNoTracking()
            where scope.AppRefIds.Contains(p.ApplicationRefId) && keys.Contains(p.PolicyKey)
            join perm in db.Permissions.AsNoTracking() on p.PermissionRefId equals perm.Id
            select new { p.PolicyKey, perm.PermissionKey, perm.Resource, perm.Action })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.PolicyKey,
            Child("PERMISSION", x.PermissionKey, $"{x.Resource}:{x.Action}", "permission", x.PermissionKey))).ToList();
    }

    private static async Task<List<(string, AccessSearchRow)>> SubjectRoles(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from a in db.Assignments.AsNoTracking()
            where scope.AppRefIds.Contains(a.ApplicationRefId) && a.State == "ACTIVE"
                && a.SubjectEmail != null && keys.Contains(a.SubjectEmail)
            join role in db.Roles.AsNoTracking() on a.RoleRefId equals role.Id
            select new { a.SubjectEmail, role.RoleKey, role.Privileged, role.RiskLevel })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.SubjectEmail!,
            Child("ROLE", x.RoleKey, $"{(x.Privileged ? "Privileged" : "Role")} · {x.RiskLevel} risk", "role", x.RoleKey))).ToList();
    }

    // Role → Policies (role → its published permissions → the policies attached to those permissions).
    // A policy can be reached more than once (e.g. a duplicate grant), so results are de-duplicated by
    // (role, policy) to keep counts honest.
    private static async Task<List<(string, AccessSearchRow)>> RolePolicies(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from rp in db.RolePermissions.AsNoTracking()
            where scope.AppRefIds.Contains(rp.ApplicationRefId) && rp.State == "PUBLISHED"
            join role in db.Roles.AsNoTracking() on rp.RoleRefId equals role.Id
            where role.Status == "ACTIVE" && keys.Contains(role.RoleKey)
            join policy in db.Policies.AsNoTracking() on rp.PermissionRefId equals policy.PermissionRefId
            where scope.AppRefIds.Contains(policy.ApplicationRefId)
            select new { role.RoleKey, policy.PolicyKey, policy.Effect, policy.State })
            .Distinct().Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.RoleKey,
            Child("POLICY", x.PolicyKey, $"{x.Effect} · {x.State}", "policy", x.PolicyKey))).ToList();
    }

    // Policy → Roles (policy → its permission → the roles that publish that permission). De-duplicated
    // by (policy, role) since a role may grant the permission through more than one grant row.
    private static async Task<List<(string, AccessSearchRow)>> PolicyRoles(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from policy in db.Policies.AsNoTracking()
            where scope.AppRefIds.Contains(policy.ApplicationRefId) && keys.Contains(policy.PolicyKey)
            join rp in db.RolePermissions.AsNoTracking() on policy.PermissionRefId equals rp.PermissionRefId
            where rp.State == "PUBLISHED" && scope.AppRefIds.Contains(rp.ApplicationRefId)
            join role in db.Roles.AsNoTracking() on rp.RoleRefId equals role.Id
            where role.Status == "ACTIVE"
            select new { policy.PolicyKey, role.RoleKey, role.Privileged, role.RiskLevel })
            .Distinct().Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.PolicyKey,
            Child("ROLE", x.RoleKey, $"{(x.Privileged ? "Privileged" : "Role")} · {x.RiskLevel} risk", "role", x.RoleKey))).ToList();
    }

    // Subject → effective Permissions (subject → active assignment → role → published permissions).
    // De-duplicated by (subject, permission) so a permission held via several roles counts once.
    private static async Task<List<(string, AccessSearchRow)>> SubjectPermissions(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from a in db.Assignments.AsNoTracking()
            where scope.AppRefIds.Contains(a.ApplicationRefId) && a.State == "ACTIVE"
                && a.SubjectEmail != null && keys.Contains(a.SubjectEmail)
            join rp in db.RolePermissions.AsNoTracking() on a.RoleRefId equals rp.RoleRefId
            where rp.State == "PUBLISHED"
            join perm in db.Permissions.AsNoTracking() on rp.PermissionRefId equals perm.Id
            where perm.Status == "ACTIVE"
            select new { a.SubjectEmail, perm.PermissionKey, perm.Resource, perm.Action })
            .Distinct().Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.SubjectEmail!,
            Child("PERMISSION", x.PermissionKey, $"{x.Resource}:{x.Action}", "permission", x.PermissionKey))).ToList();
    }

    // Subject → Applications (the applications a subject has active assignments in). De-duplicated by
    // (subject, application) since a subject usually holds several grants within one application.
    private static async Task<List<(string, AccessSearchRow)>> SubjectApplications(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from a in db.Assignments.AsNoTracking()
            where scope.AppRefIds.Contains(a.ApplicationRefId) && a.State == "ACTIVE"
                && a.SubjectEmail != null && keys.Contains(a.SubjectEmail)
            join app in db.Applications.AsNoTracking() on a.ApplicationRefId equals app.Id
            select new { a.SubjectEmail, app.ApplicationId, app.Name, app.RiskLevel, app.Status })
            .Distinct().Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.SubjectEmail!,
            Child("APPLICATION", x.Name, $"{x.RiskLevel} risk · {x.Status}", "application", x.ApplicationId))).ToList();
    }

    // ── Application/tenant relationships (keyed by applicationId / tenantId) ─────────────────────

    private static async Task<List<(string, AccessSearchRow)>> ApplicationRoles(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from role in db.Roles.AsNoTracking()
            where role.Status == "ACTIVE"
            join app in db.Applications.AsNoTracking() on role.ApplicationRefId equals app.Id
            where scope.AppIds.Contains(app.ApplicationId) && keys.Contains(app.ApplicationId)
            orderby role.RoleKey
            select new { app.ApplicationId, role.RoleKey, role.Privileged, role.RiskLevel })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.ApplicationId,
            Child("ROLE", x.RoleKey, $"{(x.Privileged ? "Privileged" : "Role")} · {x.RiskLevel} risk", "role", x.RoleKey))).ToList();
    }

    private static async Task<List<(string, AccessSearchRow)>> ApplicationPermissions(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from perm in db.Permissions.AsNoTracking()
            where perm.Status == "ACTIVE"
            join app in db.Applications.AsNoTracking() on perm.ApplicationRefId equals app.Id
            where scope.AppIds.Contains(app.ApplicationId) && keys.Contains(app.ApplicationId)
            orderby perm.PermissionKey
            select new { app.ApplicationId, perm.PermissionKey, perm.Resource, perm.Action })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.ApplicationId,
            Child("PERMISSION", x.PermissionKey, $"{x.Resource}:{x.Action}", "permission", x.PermissionKey))).ToList();
    }

    private static async Task<List<(string, AccessSearchRow)>> ApplicationPolicies(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from p in db.Policies.AsNoTracking()
            join app in db.Applications.AsNoTracking() on p.ApplicationRefId equals app.Id
            where scope.AppIds.Contains(app.ApplicationId) && keys.Contains(app.ApplicationId)
            orderby p.PolicyKey
            select new { app.ApplicationId, p.PolicyKey, p.Effect, p.State })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.ApplicationId,
            Child("POLICY", x.PolicyKey, $"{x.Effect} · {x.State}", "policy", x.PolicyKey))).ToList();
    }

    // Application → unused permissions (ASKAI-C6 derived "problem-set" relationship): permissions the
    // app defines that no PUBLISHED role grants. Exposing it as a relationship lets the existing
    // presence/absence/group/include primitives answer "applications with unused permissions", "how
    // many unused permissions per application", etc. — no new query semantics. Set-based NOT EXISTS,
    // scoped and capped.
    private static async Task<List<(string, AccessSearchRow)>> ApplicationUnusedPermissions(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from perm in db.Permissions.AsNoTracking()
            join app in db.Applications.AsNoTracking() on perm.ApplicationRefId equals app.Id
            where scope.AppIds.Contains(app.ApplicationId) && keys.Contains(app.ApplicationId)
                && !db.RolePermissions.Any(rp => rp.ApplicationRefId == app.Id && rp.State == "PUBLISHED" && rp.PermissionRefId == perm.Id)
            orderby perm.PermissionKey
            select new { app.ApplicationId, perm.PermissionKey, perm.Resource, perm.Action })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.ApplicationId,
            Child("PERMISSION", x.PermissionKey, $"{x.Resource}:{x.Action} · granted by no role", "permission", x.PermissionKey))).ToList();
    }

    // Application → unassigned roles (ASKAI-C6 derived "problem-set" relationship): ACTIVE roles the app
    // defines that no ACTIVE assignment references. Set-based NOT EXISTS, scoped and capped.
    private static async Task<List<(string, AccessSearchRow)>> ApplicationUnassignedRoles(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from role in db.Roles.AsNoTracking()
            where role.Status == "ACTIVE"
            join app in db.Applications.AsNoTracking() on role.ApplicationRefId equals app.Id
            where scope.AppIds.Contains(app.ApplicationId) && keys.Contains(app.ApplicationId)
                && !db.Assignments.Any(a => a.ApplicationRefId == app.Id && a.State == "ACTIVE" && a.RoleRefId == role.Id)
            orderby role.RoleKey
            select new { app.ApplicationId, role.RoleKey, role.Privileged, role.RiskLevel })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.ApplicationId,
            Child("ROLE", x.RoleKey, $"{(x.Privileged ? "Privileged" : "Role")} · {x.RiskLevel} risk · no active assignment", "role", x.RoleKey))).ToList();
    }

    private static async Task<List<(string, AccessSearchRow)>> ApplicationAssignments(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from a in db.Assignments.AsNoTracking()
            where a.State == "ACTIVE"
            join app in db.Applications.AsNoTracking() on a.ApplicationRefId equals app.Id
            where scope.AppIds.Contains(app.ApplicationId) && keys.Contains(app.ApplicationId)
            select new { app.ApplicationId, a.SubjectEmail, a.GroupId, a.State })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.ApplicationId,
            Child("ASSIGNMENT", x.SubjectEmail ?? x.GroupId ?? "(subject)", x.State, x.SubjectEmail != null ? "user" : null, x.SubjectEmail))).ToList();
    }

    // Application → distinct Subjects (the users with an active assignment in the application).
    // De-duplicated by (application, subject) so a user with several grants counts once.
    private static async Task<List<(string, AccessSearchRow)>> ApplicationSubjects(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from a in db.Assignments.AsNoTracking()
            where a.State == "ACTIVE" && a.SubjectEmail != null
            join app in db.Applications.AsNoTracking() on a.ApplicationRefId equals app.Id
            where scope.AppIds.Contains(app.ApplicationId) && keys.Contains(app.ApplicationId)
            select new { app.ApplicationId, a.SubjectEmail })
            .Distinct().Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.ApplicationId,
            Child("SUBJECT", x.SubjectEmail!, "user", "user", x.SubjectEmail))).ToList();
    }

    private static async Task<List<(string, AccessSearchRow)>> ApplicationOidcProviders(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from o in db.OidcProviders.AsNoTracking()
            join app in db.Applications.AsNoTracking() on o.ApplicationRefId equals app.Id
            where scope.AppIds.Contains(app.ApplicationId) && keys.Contains(app.ApplicationId)
            select new { app.ApplicationId, o.Issuer, o.ProviderType, o.Enabled })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.ApplicationId,
            Child("OIDC_PROVIDER", x.Issuer, $"{x.ProviderType} · {(x.Enabled ? "enabled" : "disabled")}", null, null))).ToList();
    }

    private static async Task<List<(string, AccessSearchRow)>> ApplicationSodRules(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from s in db.SodRules.AsNoTracking()
            join app in db.Applications.AsNoTracking() on s.ApplicationRefId equals app.Id
            where scope.AppIds.Contains(app.ApplicationId) && keys.Contains(app.ApplicationId)
            select new { app.ApplicationId, s.Name, s.Severity, s.Status })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.ApplicationId,
            Child("SOD_RULE", x.Name, $"{x.Severity} · {x.Status}", null, null))).ToList();
    }

    private static async Task<List<(string, AccessSearchRow)>> ApplicationReferenceData(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from r in db.ReferenceData.AsNoTracking()
            join app in db.Applications.AsNoTracking() on r.ApplicationRefId equals app.Id
            where scope.AppIds.Contains(app.ApplicationId) && keys.Contains(app.ApplicationId)
            select new { app.ApplicationId, r.Key, r.Status })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.ApplicationId,
            Child("REFERENCE_DATA", x.Key, x.Status, null, null))).ToList();
    }

    private static async Task<List<(string, AccessSearchRow)>> TenantApplications(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from app in db.Applications.AsNoTracking()
            join tenant in db.Tenants.AsNoTracking() on app.TenantRefId equals tenant.Id
            where scope.AppIds.Contains(app.ApplicationId) && keys.Contains(tenant.TenantId)
            orderby app.Name
            select new { tenant.TenantId, app.ApplicationId, app.Name, app.RiskLevel, app.Status })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.TenantId,
            Child("APPLICATION", x.Name, $"{x.RiskLevel} risk · {x.Status}", "application", x.ApplicationId))).ToList();
    }

    // Application → Tenant (reverse of Tenant → Applications): resolves the single owning tenant for
    // an application, so "which tenant owns application X" can be answered from the application side
    // — application has no tenant-name filter field, so it cannot be answered by filtering tenant
    // directly (see HierarchyOverview).
    private static async Task<List<(string, AccessSearchRow)>> ApplicationTenant(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from app in db.Applications.AsNoTracking()
            join tenant in db.Tenants.AsNoTracking() on app.TenantRefId equals tenant.Id
            where scope.AppIds.Contains(app.ApplicationId) && keys.Contains(app.ApplicationId)
            select new { app.ApplicationId, tenant.TenantId, tenant.Name, tenant.Status })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.ApplicationId,
            Child("TENANT", x.Name, $"{x.Status} · {x.TenantId}", "tenant", x.TenantId))).ToList();
    }

    // Tenant → Roles (two-hop: tenant → application → role). A tenant owns its roles transitively
    // through its applications, so these join through Application and key each child by the owning
    // tenant. The scope filter keeps results within the caller's accessible applications, and the
    // child rows carry their real application id so tree deep-links resolve. Same pattern for the
    // permission/policy/assignment roll-ups below.
    private static async Task<List<(string, AccessSearchRow)>> TenantRoles(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from role in db.Roles.AsNoTracking()
            where role.Status == "ACTIVE"
            join app in db.Applications.AsNoTracking() on role.ApplicationRefId equals app.Id
            where scope.AppIds.Contains(app.ApplicationId)
            join tenant in db.Tenants.AsNoTracking() on app.TenantRefId equals tenant.Id
            where keys.Contains(tenant.TenantId)
            orderby role.RoleKey
            select new { tenant.TenantId, app.ApplicationId, role.RoleKey, role.Privileged, role.RiskLevel })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.TenantId, new AccessSearchRow(
            x.ApplicationId, "ROLE", x.RoleKey,
            $"{(x.Privileged ? "Privileged" : "Role")} · {x.RiskLevel} risk · {x.ApplicationId}", "role", x.RoleKey))).ToList();
    }

    // Tenant → Permissions (tenant → application → permission).
    private static async Task<List<(string, AccessSearchRow)>> TenantPermissions(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from perm in db.Permissions.AsNoTracking()
            where perm.Status == "ACTIVE"
            join app in db.Applications.AsNoTracking() on perm.ApplicationRefId equals app.Id
            where scope.AppIds.Contains(app.ApplicationId)
            join tenant in db.Tenants.AsNoTracking() on app.TenantRefId equals tenant.Id
            where keys.Contains(tenant.TenantId)
            orderby perm.PermissionKey
            select new { tenant.TenantId, app.ApplicationId, perm.PermissionKey, perm.Resource, perm.Action })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.TenantId, new AccessSearchRow(
            x.ApplicationId, "PERMISSION", x.PermissionKey, $"{x.Resource}:{x.Action} · {x.ApplicationId}", "permission", x.PermissionKey))).ToList();
    }

    // Tenant → Policies (tenant → application → policy).
    private static async Task<List<(string, AccessSearchRow)>> TenantPolicies(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from p in db.Policies.AsNoTracking()
            join app in db.Applications.AsNoTracking() on p.ApplicationRefId equals app.Id
            where scope.AppIds.Contains(app.ApplicationId)
            join tenant in db.Tenants.AsNoTracking() on app.TenantRefId equals tenant.Id
            where keys.Contains(tenant.TenantId)
            orderby p.PolicyKey
            select new { tenant.TenantId, app.ApplicationId, p.PolicyKey, p.Effect, p.State })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.TenantId, new AccessSearchRow(
            x.ApplicationId, "POLICY", x.PolicyKey, $"{x.Effect} · {x.State} · {x.ApplicationId}", "policy", x.PolicyKey))).ToList();
    }

    // Tenant → Assignments (tenant → application → active assignment).
    private static async Task<List<(string, AccessSearchRow)>> TenantAssignments(
        AuthorizationDbContext db, AccessScope scope, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var rows = await (
            from a in db.Assignments.AsNoTracking()
            where a.State == "ACTIVE"
            join app in db.Applications.AsNoTracking() on a.ApplicationRefId equals app.Id
            where scope.AppIds.Contains(app.ApplicationId)
            join tenant in db.Tenants.AsNoTracking() on app.TenantRefId equals tenant.Id
            where keys.Contains(tenant.TenantId)
            select new { tenant.TenantId, app.ApplicationId, a.SubjectEmail, a.GroupId, a.State })
            .Take(FetchCap).ToListAsync(ct);
        return rows.Select(x => (x.TenantId, new AccessSearchRow(
            x.ApplicationId, "ASSIGNMENT", x.SubjectEmail ?? x.GroupId ?? "(subject)", $"{x.State} · {x.ApplicationId}",
            x.SubjectEmail != null ? "user" : null, x.SubjectEmail))).ToList();
    }
}
