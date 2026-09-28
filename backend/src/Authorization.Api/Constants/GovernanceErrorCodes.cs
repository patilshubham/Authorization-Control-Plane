namespace Authorization.Api.Constants;

/// <summary>
/// Stable machine-readable error codes returned in <c>ApiError.Code</c> by the
/// governance API. Clients switch on these codes, so the string values must not
/// change.
/// </summary>
public static class GovernanceErrorCodes
{
    public const string ValidationError = "VALIDATION_ERROR";
    public const string Forbidden = "FORBIDDEN";

    public const string TenantExists = "TENANT_EXISTS";
    public const string TenantNotFound = "TENANT_NOT_FOUND";
    public const string TenantInUse = "TENANT_IN_USE";

    public const string ApplicationExists = "APPLICATION_EXISTS";
    public const string ApplicationNotFound = "APPLICATION_NOT_FOUND";

    public const string OidcProviderNotFound = "OIDC_PROVIDER_NOT_FOUND";

    public const string RoleExists = "ROLE_EXISTS";
    public const string RoleNotFound = "ROLE_NOT_FOUND";
    public const string RoleInUse = "ROLE_IN_USE";

    public const string PermissionExists = "PERMISSION_EXISTS";
    public const string PermissionNotFound = "PERMISSION_NOT_FOUND";
    public const string PermissionInUse = "PERMISSION_IN_USE";

    public const string RolePermissionNotFound = "ROLE_PERMISSION_NOT_FOUND";

    public const string PrivilegedAssignmentRequiresExpiry = "PRIVILEGED_ASSIGNMENT_REQUIRES_EXPIRY";
    public const string AssignmentNotFound = "ASSIGNMENT_NOT_FOUND";
    public const string AssignmentRevoked = "ASSIGNMENT_REVOKED";
    public const string AssignmentExists = "ASSIGNMENT_EXISTS";

    public const string PolicyConditionsInvalid = "POLICY_CONDITIONS_INVALID";
    public const string PolicyNotFound = "POLICY_NOT_FOUND";
    public const string PolicyNotEditable = "POLICY_NOT_EDITABLE";
    public const string PolicyObligationsInvalid = "POLICY_OBLIGATIONS_INVALID";

    public const string ReferenceDataExists = "REFERENCE_DATA_EXISTS";
    public const string ReferenceDataNotFound = "REFERENCE_DATA_NOT_FOUND";
    public const string ReferenceDataValueInvalid = "REFERENCE_DATA_VALUE_INVALID";
}
