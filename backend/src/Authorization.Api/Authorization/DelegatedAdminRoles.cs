namespace Authorization.Api.Authorization;

public static class DelegatedAdminRoles
{
    public const string PlatformSuperAdmin = "PlatformSuperAdmin";
    public const string PlatformReadOnlyViewer = "PlatformReadOnlyViewer";
    public const string ApplicationAdmin = "ApplicationAdmin";
    public const string ReadOnlyViewer = "ReadOnlyViewer";

    // Tenant-scoped delegated administration: these grant the same capabilities as their
    // application-scoped counterparts, but across every application owned by the tenant.
    public const string TenantAdmin = "TenantAdmin";
    public const string TenantReadOnlyViewer = "TenantReadOnlyViewer";
}
