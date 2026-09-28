namespace Authorization.Api.Authorization;

public enum DelegatedAdminCapability
{
    ManageApplication,
    ManageRoles,
    ManagePermissions,
    MapRolePermission,
    ManagePolicies,
    AssignRoles,
    ViewAudit,
    ReadOnlyView,
}