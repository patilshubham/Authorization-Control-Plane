using Authorization.Infrastructure.Persistence;

namespace Authorization.Api.Contracts;

public sealed record ApplicationResponse(
    Guid Id,
    string ApplicationId,
    string Name,
    string? Description,
    string? TenantId,
    string? OwnerTeam,
    string? BusinessOwner,
    string? TechnicalOwner,
    string RiskLevel,
    string Status,
    string SourceOfTruthMode,
    string PolicyCombiningAlgorithm,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy,
    int Version)
{
    public static ApplicationResponse From(ApplicationEntity entity, string? tenantId) => new(
        entity.Id,
        entity.ApplicationId,
        entity.Name,
        entity.Description,
        tenantId,
        entity.OwnerTeam,
        entity.BusinessOwner,
        entity.TechnicalOwner,
        entity.RiskLevel,
        entity.Status,
        entity.SourceOfTruthMode,
        entity.PolicyCombiningAlgorithm,
        entity.CreatedAt,
        entity.CreatedBy,
        entity.UpdatedAt,
        entity.UpdatedBy,
        entity.Version);
}

public sealed record RolePermissionResponse(
    Guid Id,
    string RoleKey,
    string PermissionKey,
    string State,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CreatedAt,
    string CreatedBy)
{
    public static RolePermissionResponse From(RolePermissionEntity entity, string roleKey, string permissionKey) => new(
        entity.Id,
        roleKey,
        permissionKey,
        entity.State,
        entity.ValidFrom,
        entity.PublishedAt,
        entity.CreatedAt,
        entity.CreatedBy);
}

public sealed record AssignmentResponse(
    Guid Id,
    string SubjectType,
    string? SubjectEmail,
    string? GroupId,
    string RoleKey,
    string? ResourceType,
    string? ResourceId,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidUntil,
    DateTimeOffset? RevokedAt,
    string Source,
    string State,
    string? Reason,
    DateTimeOffset CreatedAt,
    string CreatedBy)
{
    public static AssignmentResponse From(AssignmentEntity entity, string roleKey) => new(
        entity.Id,
        entity.SubjectType,
        entity.SubjectEmail,
        entity.GroupId,
        roleKey,
        entity.ResourceType,
        entity.ResourceId,
        entity.ValidFrom,
        entity.ValidUntil,
        entity.RevokedAt,
        entity.Source,
        entity.State,
        entity.Reason,
        entity.CreatedAt,
        entity.CreatedBy);
}

/// <summary>Count of assignments in a single display state (ACTIVE / EXPIRED / REVOKED).</summary>
public sealed record AssignmentStateCount(string State, int Count);

/// <summary>
/// Aggregate view of an application's assignments used to drive charts and totals without
/// pulling every row. Complements the paged <see cref="AssignmentResponse"/> list.
/// </summary>
public sealed record AssignmentSummaryResponse(int Total, IReadOnlyList<AssignmentStateCount> States);

public sealed record PolicyResponse(
    Guid Id,
    string PolicyKey,
    string PermissionKey,
    string Effect,
    string Conditions,
    int Priority,
    string Obligations,
    string State,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CreatedAt,
    string CreatedBy)
{
    public static PolicyResponse From(PolicyEntity entity, string permissionKey) => new(
        entity.Id,
        entity.PolicyKey,
        permissionKey,
        entity.Effect,
        entity.Conditions,
        entity.Priority,
        entity.Obligations,
        entity.State,
        entity.PublishedAt,
        entity.CreatedAt,
        entity.CreatedBy);
}

public sealed record ReferenceDataResponse(
    Guid Id,
    string Key,
    string? Description,
    string Value,
    string Status,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy,
    int Version)
{
    public static ReferenceDataResponse From(ReferenceDataEntity entity) => new(
        entity.Id,
        entity.Key,
        entity.Description,
        entity.Value,
        entity.Status,
        entity.CreatedAt,
        entity.CreatedBy,
        entity.UpdatedAt,
        entity.UpdatedBy,
        entity.Version);
}

public sealed record RoleResponse(
    Guid Id,
    string ApplicationId,
    string RoleKey,
    string Name,
    string? Description,
    bool Privileged,
    string RiskLevel,
    string Status,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy,
    int Version)
{
    public static RoleResponse From(RoleEntity entity, string applicationId) => new(
        entity.Id,
        applicationId,
        entity.RoleKey,
        entity.Name,
        entity.Description,
        entity.Privileged,
        entity.RiskLevel,
        entity.Status,
        entity.CreatedAt,
        entity.CreatedBy,
        entity.UpdatedAt,
        entity.UpdatedBy,
        entity.Version);
}

public sealed record PermissionResponse(
    Guid Id,
    string ApplicationId,
    string PermissionKey,
    string Resource,
    string Action,
    string? Description,
    string RiskLevel,
    string Status,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy,
    int Version)
{
    public static PermissionResponse From(PermissionEntity entity, string applicationId) => new(
        entity.Id,
        applicationId,
        entity.PermissionKey,
        entity.Resource,
        entity.Action,
        entity.Description,
        entity.RiskLevel,
        entity.Status,
        entity.CreatedAt,
        entity.CreatedBy,
        entity.UpdatedAt,
        entity.UpdatedBy,
        entity.Version);
}

public sealed record OidcProviderResponse(
    Guid Id,
    string ApplicationId,
    string ProviderType,
    string Issuer,
    string Audience,
    string JwksUri,
    string[] AllowedAlgorithms,
    string RequiredClaims,
    string ClaimMappings,
    string SubjectType,
    string SubjectClaim,
    bool Enabled,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy,
    int Version)
{
    public static OidcProviderResponse From(OidcProviderEntity entity, string applicationId) => new(
        entity.Id,
        applicationId,
        entity.ProviderType,
        entity.Issuer,
        entity.Audience,
        entity.JwksUri,
        entity.AllowedAlgorithms,
        entity.RequiredClaims,
        entity.ClaimMappings,
        entity.SubjectType,
        entity.SubjectClaim,
        entity.Enabled,
        entity.CreatedAt,
        entity.CreatedBy,
        entity.UpdatedAt,
        entity.UpdatedBy,
        entity.Version);
}

public sealed record RoleWithPermissionsResponse(
    string RoleKey,
    string Name,
    bool Privileged,
    string RiskLevel,
    string Status,
    IReadOnlyList<string> Permissions,
    string? Description = null);

public sealed record TenantResponse(
    Guid Id,
    string TenantId,
    string Name,
    string? Description,
    string Status,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy,
    int Version)
{
    public static TenantResponse From(TenantEntity entity) => new(
        entity.Id,
        entity.TenantId,
        entity.Name,
        entity.Description,
        entity.Status,
        entity.CreatedAt,
        entity.CreatedBy,
        entity.UpdatedAt,
        entity.UpdatedBy,
        entity.Version);
}

public sealed record AuditEventResponse(
    Guid EventId,
    string EventType,
    string? ApplicationId,
    string? ActorEmail,
    string? ActorRole,
    string? ActorClientId,
    string? TargetSubjectEmail,
    string? SourceIp,
    DateTimeOffset Timestamp,
    string? OldValue,
    string? NewValue,
    string? Reason,
    string? CorrelationId)
{
    public static AuditEventResponse From(AuditEventEntity entity) => new(
        entity.EventId,
        entity.EventType,
        entity.ApplicationId,
        entity.ActorEmail,
        entity.ActorRole,
        entity.ActorClientId,
        entity.TargetSubjectEmail,
        entity.SourceIp?.ToString(),
        entity.Timestamp,
        entity.OldValue,
        entity.NewValue,
        entity.Reason,
        entity.CorrelationId);
}

/// <summary>A single day bucket of audit activity (UTC date, yyyy-MM-dd) with its event count.</summary>
public sealed record AuditActivityDay(string Date, int Count);

/// <summary>
/// Lightweight aggregate powering the audit/activity calendar heatmap: total events in the
/// window plus per-day counts. Lets the feed paginate without the heatmap needing every row.
/// </summary>
public sealed record AuditActivitySummaryResponse(int Total, IReadOnlyList<AuditActivityDay> Days);

public sealed record TenantApplicationSummary(
    string ApplicationId,
    string Name,
    string Status,
    string RiskLevel,
    int RoleCount,
    int AssignmentCount,
    int ActiveAssignmentCount);

public sealed record TenantRollup(int ApplicationCount, int RoleCount, int AssignmentCount, int ActiveAssignmentCount);

public sealed record TenantDetailResponse(TenantResponse Tenant, IReadOnlyList<TenantApplicationSummary> Applications, TenantRollup Rollup);

public sealed record TenantApplicationCount(string TenantId, string TenantName, int ApplicationCount);

public sealed record PlatformOverviewResponse(
    int TenantCount,
    int ApplicationCount,
    int RoleCount,
    int PermissionCount,
    int PolicyCount,
    int AssignmentCount,
    int ActiveAssignmentCount,
    IReadOnlyDictionary<string, int> ApplicationsByRisk,
    IReadOnlyList<TenantApplicationCount> ApplicationsByTenant,
    IReadOnlyList<AuditEventResponse> RecentAudit);

public sealed record ApplicationOverviewResponse(
    string ApplicationId,
    string Name,
    int RoleCount,
    int PrivilegedRoleCount,
    int PermissionCount,
    int PolicyCount,
    int PublishedPolicyCount,
    int DraftPolicyCount,
    int AssignmentCount,
    int ActiveAssignmentCount,
    IReadOnlyDictionary<string, int> RoleRiskDistribution,
    IReadOnlyList<AuditEventResponse> RecentAudit);

public sealed record UserDirectoryEntry(
    string Email,
    int ApplicationCount,
    int AssignmentCount,
    int ActiveCount,
    int ExpiredCount,
    int RevokedCount);

public sealed record UserAccessAssignment(string ApplicationId, string RoleKey, string State, DateTimeOffset? ValidUntil);

public sealed record UserAccessResponse(string Email, IReadOnlyList<UserAccessAssignment> Assignments);

/// <summary>A single change in a policy's timeline, reconstructed from the audit log.</summary>
public sealed record PolicyHistoryEntry(
    string EventType,
    string Actor,
    string? ActorRole,
    DateTimeOffset Timestamp,
    string? OldValue,
    string? NewValue);

public sealed record PolicyHistoryResponse(IReadOnlyList<PolicyHistoryEntry> Entries);
