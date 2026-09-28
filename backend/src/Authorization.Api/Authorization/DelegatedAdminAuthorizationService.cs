using System.Security.Claims;

namespace Authorization.Api.Authorization;

public sealed class DelegatedAdminAuthorizationService
{
    private static readonly IReadOnlyDictionary<DelegatedAdminCapability, IReadOnlySet<string>> CapabilityRoles =
        new Dictionary<DelegatedAdminCapability, IReadOnlySet<string>>
        {
            [DelegatedAdminCapability.ManageApplication] = RoleSet(
                DelegatedAdminRoles.ApplicationAdmin),
            [DelegatedAdminCapability.ManageRoles] = RoleSet(
                DelegatedAdminRoles.ApplicationAdmin),
            [DelegatedAdminCapability.ManagePermissions] = RoleSet(
                DelegatedAdminRoles.ApplicationAdmin),
            [DelegatedAdminCapability.MapRolePermission] = RoleSet(
                DelegatedAdminRoles.ApplicationAdmin),
            [DelegatedAdminCapability.ManagePolicies] = RoleSet(
                DelegatedAdminRoles.ApplicationAdmin),
            [DelegatedAdminCapability.AssignRoles] = RoleSet(
                DelegatedAdminRoles.ApplicationAdmin),
            [DelegatedAdminCapability.ViewAudit] = RoleSet(
                DelegatedAdminRoles.ApplicationAdmin,
                DelegatedAdminRoles.ReadOnlyViewer),
            [DelegatedAdminCapability.ReadOnlyView] = RoleSet(
                DelegatedAdminRoles.ApplicationAdmin,
                DelegatedAdminRoles.ReadOnlyViewer),
        };

    // Tenant-scoped roles mirror their application-scoped counterparts: TenantAdmin grants
    // every capability across the tenant's applications, TenantReadOnlyViewer grants read/audit.
    private static readonly IReadOnlyDictionary<DelegatedAdminCapability, IReadOnlySet<string>> TenantCapabilityRoles =
        new Dictionary<DelegatedAdminCapability, IReadOnlySet<string>>
        {
            [DelegatedAdminCapability.ManageApplication] = RoleSet(
                DelegatedAdminRoles.TenantAdmin),
            [DelegatedAdminCapability.ManageRoles] = RoleSet(
                DelegatedAdminRoles.TenantAdmin),
            [DelegatedAdminCapability.ManagePermissions] = RoleSet(
                DelegatedAdminRoles.TenantAdmin),
            [DelegatedAdminCapability.MapRolePermission] = RoleSet(
                DelegatedAdminRoles.TenantAdmin),
            [DelegatedAdminCapability.ManagePolicies] = RoleSet(
                DelegatedAdminRoles.TenantAdmin),
            [DelegatedAdminCapability.AssignRoles] = RoleSet(
                DelegatedAdminRoles.TenantAdmin),
            [DelegatedAdminCapability.ViewAudit] = RoleSet(
                DelegatedAdminRoles.TenantAdmin,
                DelegatedAdminRoles.TenantReadOnlyViewer),
            [DelegatedAdminCapability.ReadOnlyView] = RoleSet(
                DelegatedAdminRoles.TenantAdmin,
                DelegatedAdminRoles.TenantReadOnlyViewer),
        };

    public bool IsAuthorized(ClaimsPrincipal user, string applicationId, DelegatedAdminCapability capability)
    {
        if (string.IsNullOrWhiteSpace(applicationId) || user.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (user.IsInRole(DelegatedAdminRoles.PlatformSuperAdmin))
        {
            return true;
        }

        if (user.IsInRole(DelegatedAdminRoles.PlatformReadOnlyViewer))
        {
            return capability is DelegatedAdminCapability.ViewAudit or DelegatedAdminCapability.ReadOnlyView;
        }

        return CapabilityRoles[capability].Any(requiredRole => HasApplicationRole(user, applicationId, requiredRole));
    }

    /// <summary>
    /// Application-or-tenant authorization: succeeds when the caller holds a matching
    /// platform/application role, or a tenant-scoped role for the application's owning tenant.
    /// Callers that already know the application's tenant (list/scoping code) use this overload.
    /// </summary>
    public bool IsAuthorized(ClaimsPrincipal user, string applicationId, string? tenantId, DelegatedAdminCapability capability)
    {
        return IsAuthorized(user, applicationId, capability)
            || IsAuthorizedForTenant(user, tenantId, capability);
    }

    /// <summary>Whether the caller holds a tenant-scoped role granting <paramref name="capability"/> for the tenant.</summary>
    public bool IsAuthorizedForTenant(ClaimsPrincipal user, string? tenantId, DelegatedAdminCapability capability)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || user.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        return TenantCapabilityRoles[capability].Any(requiredRole => HasTenantRole(user, tenantId, requiredRole));
    }

    /// <summary>Cheap check used to avoid a tenant lookup for callers that hold no tenant roles at all.</summary>
    public bool HasAnyTenantRole(ClaimsPrincipal user)
    {
        return user.Identity?.IsAuthenticated == true
            && user.Claims.Any(claim => claim.Type == DelegatedAdminClaimTypes.TenantRole);
    }

    /// <summary>The set of tenant ids the caller holds any tenant-scoped role for.</summary>
    public IReadOnlySet<string> GetTenantScopedTenantIds(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return user.Claims
            .Where(claim => claim.Type == DelegatedAdminClaimTypes.TenantRole)
            .Select(claim => claim.Value)
            .Select(value =>
            {
                int separator = value.LastIndexOf(':');
                return separator > 0 ? value[..separator] : null;
            })
            .Where(tenantId => !string.IsNullOrWhiteSpace(tenantId))
            .Select(tenantId => tenantId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool CanAccessAllApplications(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        return user.IsInRole(DelegatedAdminRoles.PlatformSuperAdmin)
            || user.IsInRole(DelegatedAdminRoles.PlatformReadOnlyViewer);
    }

    public static string CreateApplicationRoleValue(string applicationId, string role)
    {
        return $"{applicationId}:{role}";
    }

    public static string CreateTenantRoleValue(string tenantId, string role)
    {
        return $"{tenantId}:{role}";
    }

    private static bool HasApplicationRole(ClaimsPrincipal user, string applicationId, string role)
    {
        string expectedValue = CreateApplicationRoleValue(applicationId, role);

        return user.Claims.Any(claim =>
            claim.Type == DelegatedAdminClaimTypes.AppRole
            && claim.Value.Equals(expectedValue, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasTenantRole(ClaimsPrincipal user, string tenantId, string role)
    {
        string expectedValue = CreateTenantRoleValue(tenantId, role);

        return user.Claims.Any(claim =>
            claim.Type == DelegatedAdminClaimTypes.TenantRole
            && claim.Value.Equals(expectedValue, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlySet<string> RoleSet(params string[] roles)
    {
        return roles.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
