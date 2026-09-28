namespace Authorization.Api.Authorization;

public static class DelegatedAdminPolicyNames
{
    public const string Prefix = "DelegatedAdmin";

    public const string PlatformAdmin = "PlatformAdmin";

    // Coarse controller-level gate for the whole admin plane. Distinct from the fine-grained
    // DelegatedAdmin:* capability policies below, which authorize individual mutations.
    public const string AdminApi = "AdminApi";

    public const string ManageApplication = $"{Prefix}:{nameof(DelegatedAdminCapability.ManageApplication)}";
    public const string ManageRoles = $"{Prefix}:{nameof(DelegatedAdminCapability.ManageRoles)}";
    public const string ManagePermissions = $"{Prefix}:{nameof(DelegatedAdminCapability.ManagePermissions)}";
    public const string MapRolePermission = $"{Prefix}:{nameof(DelegatedAdminCapability.MapRolePermission)}";
    public const string ManagePolicies = $"{Prefix}:{nameof(DelegatedAdminCapability.ManagePolicies)}";
    public const string AssignRoles = $"{Prefix}:{nameof(DelegatedAdminCapability.AssignRoles)}";
    public const string ReadOnlyView = $"{Prefix}:{nameof(DelegatedAdminCapability.ReadOnlyView)}";

    public static string For(DelegatedAdminCapability capability)
    {
        return $"{Prefix}:{capability}";
    }
}
