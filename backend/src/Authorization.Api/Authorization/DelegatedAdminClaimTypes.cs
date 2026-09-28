namespace Authorization.Api.Authorization;

public static class DelegatedAdminClaimTypes
{
    public const string AppRole = "acp_app_role";
    public const string PlatformRole = "acp_platform_role";

    /// <summary>Tenant-scoped delegated-admin role claim, formatted as "{tenantId}:{role}".</summary>
    public const string TenantRole = "acp_tenant_role";
}

public static class AuditActorRole
{
    /// <summary>
    /// Resolves a display role for the acting principal from its platform/app role claims.
    /// Returns the raw role identifier (for example "PlatformSuperAdmin", or the role part of
    /// an "{appId}:{role}" app-role claim). The portal maps this identifier to a friendly label.
    /// </summary>
    public static string? Resolve(System.Security.Claims.ClaimsPrincipal user)
    {
        string? platformRole = user.Claims
            .FirstOrDefault(claim => claim.Type == DelegatedAdminClaimTypes.PlatformRole)?.Value;
        if (!string.IsNullOrWhiteSpace(platformRole))
        {
            return platformRole;
        }

        string? appRole = user.Claims
            .FirstOrDefault(claim => claim.Type == DelegatedAdminClaimTypes.AppRole)?.Value;
        if (!string.IsNullOrWhiteSpace(appRole))
        {
            int separator = appRole.LastIndexOf(':');
            return separator >= 0 && separator < appRole.Length - 1
                ? appRole[(separator + 1)..]
                : appRole;
        }

        string? tenantRole = user.Claims
            .FirstOrDefault(claim => claim.Type == DelegatedAdminClaimTypes.TenantRole)?.Value;
        if (!string.IsNullOrWhiteSpace(tenantRole))
        {
            int separator = tenantRole.LastIndexOf(':');
            return separator >= 0 && separator < tenantRole.Length - 1
                ? tenantRole[(separator + 1)..]
                : tenantRole;
        }

        return null;
    }
}
