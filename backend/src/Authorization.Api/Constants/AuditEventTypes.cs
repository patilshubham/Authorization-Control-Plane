namespace Authorization.Api.Constants;

/// <summary>
/// Canonical audit event type identifiers emitted by the governance API. These
/// values are persisted verbatim to the audit log and consumed by reporting
/// tooling, so they must remain stable.
/// </summary>
public static class AuditEventTypes
{
    public const string TenantCreated = "TENANT_CREATED";
    public const string TenantUpdated = "TENANT_UPDATED";
    public const string TenantDeleted = "TENANT_DELETED";

    public const string ApplicationCreated = "APPLICATION_CREATED";
    public const string ApplicationUpdated = "APPLICATION_UPDATED";
    public const string ApplicationActivated = "APPLICATION_ACTIVATED";
    public const string ApplicationDisabled = "APPLICATION_DISABLED";
    public const string ApplicationArchived = "APPLICATION_ARCHIVED";

    public const string OidcProviderCreated = "OIDC_PROVIDER_CREATED";
    public const string OidcProviderUpdated = "OIDC_PROVIDER_UPDATED";

    public const string RoleCreated = "ROLE_CREATED";
    public const string RoleUpdated = "ROLE_UPDATED";
    public const string RoleDeleted = "ROLE_DELETED";
    public const string RoleActivated = "ROLE_ACTIVATED";
    public const string RoleDisabled = "ROLE_DISABLED";
    public const string RoleArchived = "ROLE_ARCHIVED";

    public const string PermissionCreated = "PERMISSION_CREATED";
    public const string PermissionUpdated = "PERMISSION_UPDATED";
    public const string PermissionDeleted = "PERMISSION_DELETED";
    public const string PermissionActivated = "PERMISSION_ACTIVATED";
    public const string PermissionDisabled = "PERMISSION_DISABLED";
    public const string PermissionArchived = "PERMISSION_ARCHIVED";

    public const string RolePermissionCreated = "ROLE_PERMISSION_CREATED";
    public const string RolePermissionPublished = "ROLE_PERMISSION_PUBLISHED";
    public const string RolePermissionRevoked = "ROLE_PERMISSION_REVOKED";

    public const string AssignmentCreated = "ASSIGNMENT_CREATED";
    public const string AssignmentRevoked = "ASSIGNMENT_REVOKED";
    public const string AssignmentExtended = "ASSIGNMENT_EXTENDED";
    public const string AssignmentUpdated = "ASSIGNMENT_UPDATED";
    public const string AssignmentAttributeCreated = "ASSIGNMENT_ATTRIBUTE_CREATED";
    public const string BreakGlassActivated = "BREAK_GLASS_ACTIVATED";

    public const string PolicyCreated = "POLICY_CREATED";
    public const string PolicyPublished = "POLICY_PUBLISHED";
    public const string PolicyUpdated = "POLICY_UPDATED";
    public const string PolicyDeleted = "POLICY_DELETED";

    public const string ReferenceDataCreated = "REFERENCE_DATA_CREATED";
    public const string ReferenceDataUpdated = "REFERENCE_DATA_UPDATED";
    public const string ReferenceDataDeleted = "REFERENCE_DATA_DELETED";

    public const string ReviewCampaignCreated = "REVIEW_CAMPAIGN_CREATED";
    public const string ReviewCampaignActivated = "REVIEW_CAMPAIGN_ACTIVATED";
    public const string ReviewCampaignFinalized = "REVIEW_CAMPAIGN_FINALIZED";
}
