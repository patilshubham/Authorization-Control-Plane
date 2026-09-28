using System.ComponentModel.DataAnnotations;

namespace Authorization.Api.Contracts;

public sealed record CreateApplicationRequest(
    [Required] string ApplicationId,
    [Required] string Name,
    string? Description,
    [Required] string TenantId,
    string? OwnerTeam,
    string? BusinessOwner,
    string? TechnicalOwner,
    [AllowedValues("LOW", "MEDIUM", "HIGH", "CRITICAL")] string RiskLevel = "MEDIUM",
    [AllowedValues("deny-overrides", "allow-overrides", "first-applicable")] string PolicyCombiningAlgorithm = "deny-overrides",
    string SourceOfTruthMode = "PLATFORM_OWNED");

public sealed record CreateTenantRequest([Required] string TenantId, [Required] string Name, string? Description);

public sealed record UpdateTenantRequest([Required] string Name, string? Description, string? Status);

public sealed record UpdateApplicationRequest(
    [Required] string Name,
    string? Description,
    [Required] string TenantId,
    string? OwnerTeam,
    string? BusinessOwner,
    string? TechnicalOwner,
    [AllowedValues("LOW", "MEDIUM", "HIGH", "CRITICAL")] string RiskLevel = "MEDIUM",
    [AllowedValues("deny-overrides", "allow-overrides", "first-applicable")] string PolicyCombiningAlgorithm = "deny-overrides",
    [AllowedValues("PLATFORM_OWNED", "EXTERNAL_READ", "DUAL_WRITE", "EXTERNAL_OWNED")] string SourceOfTruthMode = "PLATFORM_OWNED");

public sealed record CreateOidcProviderRequest([Required] string Issuer, [Required] string Audience, [Required] string JwksUri, string[] AllowedAlgorithms, string RequiredClaims = "{}", string ClaimMappings = "{}", [AllowedValues("USER", "SERVICE_ACCOUNT")] string SubjectType = "USER", string SubjectClaim = "sub");

public sealed record UpdateOidcProviderRequest([Required] string Issuer, [Required] string Audience, [Required] string JwksUri, string[] AllowedAlgorithms, bool Enabled = true, [AllowedValues("USER", "SERVICE_ACCOUNT")] string SubjectType = "USER", string SubjectClaim = "sub");

public sealed record CreateRoleRequest([Required] string RoleKey, [Required] string Name, string? Description, bool Privileged, [AllowedValues("LOW", "MEDIUM", "HIGH", "CRITICAL")] string RiskLevel = "MEDIUM");

public sealed record UpdateRoleRequest([Required] string Name, string? Description, bool Privileged, [AllowedValues("LOW", "MEDIUM", "HIGH", "CRITICAL")] string RiskLevel = "MEDIUM");

public sealed record CreatePermissionRequest([Required] string PermissionKey, [Required] string Resource, [Required] string Action, string? Description, [AllowedValues("LOW", "MEDIUM", "HIGH", "CRITICAL")] string RiskLevel = "MEDIUM");

public sealed record UpdatePermissionRequest(string? Description, [AllowedValues("LOW", "MEDIUM", "HIGH", "CRITICAL")] string RiskLevel = "MEDIUM");

public sealed record CreateRolePermissionRequest([Required] string RoleKey, [Required] string PermissionKey, bool Publish = false);

public sealed record CreateAssignmentRequest(
    [Required] string SubjectType,
    string? SubjectEmail,
    string? GroupId,
    [Required] string RoleKey,
    string? ResourceType,
    string? ResourceId,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil,
    string Source = "MANUAL",
    string? Reason = null);

public sealed record ExtendAssignmentRequest(DateTimeOffset ValidUntil);

public sealed record UpdateAssignmentRequest(
    string? RoleKey,
    DateTimeOffset? ValidUntil,
    string? Reason);

public sealed record CreateAssignmentAttributeRequest([Required] string Name, [Required] string ValueJson, [Required] string ValueType);

public sealed record CreatePolicyRequest([Required] string PolicyKey, [Required] string PermissionKey, [Required] string Effect, [Required] string Conditions, int Priority = 0, string Obligations = "[]", bool Publish = false);

public sealed record UpdatePolicyRequest([Required] string Effect, [Required] string Conditions, int Priority = 0, string Obligations = "[]", bool Publish = false);

public sealed record CreateReferenceDataRequest([Required] string Key, string? Description, [Required] string Value);

public sealed record UpdateReferenceDataRequest(string? Description, [Required] string Value);

public sealed record SimulatorAuthorizeRequest(
    [Required] string ApplicationId,
    [Required] string SubjectType,
    string? SubjectEmail,
    [Required] string ResourceType,
    string? ResourceId,
    [Required] string Action,
    IReadOnlyDictionary<string, object?>? Context);
