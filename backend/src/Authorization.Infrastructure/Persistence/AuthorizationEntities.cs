using System.Net;

namespace Authorization.Infrastructure.Persistence;

public abstract class AuditedEntity
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "system";
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public int Version { get; set; }
}

public sealed class TenantEntity : AuditedEntity
{
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Status { get; set; } = "ACTIVE";
}

public sealed class ApplicationEntity : AuditedEntity
{
    public string ApplicationId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>Guid FK to the owning tenant's row id. Required root of the ownership hierarchy.</summary>
    public Guid TenantRefId { get; set; }
    public string? OwnerTeam { get; set; }
    public string? BusinessOwner { get; set; }
    public string? TechnicalOwner { get; set; }
    public string RiskLevel { get; set; } = "MEDIUM";
    public string Status { get; set; } = "ACTIVE";
    public string SourceOfTruthMode { get; set; } = "PLATFORM_OWNED";

    /// <summary>
    /// How the runtime engine combines the ALLOW/DENY policies that match a request:
    /// <c>deny-overrides</c> (default — any matching DENY wins), <c>allow-overrides</c> (any
    /// matching ALLOW wins), or <c>first-applicable</c> (the highest-priority matching policy
    /// decides). Defaulting to <c>deny-overrides</c> preserves the historical hardcoded behavior.
    /// </summary>
    public string PolicyCombiningAlgorithm { get; set; } = "deny-overrides";
}

public sealed class OidcProviderEntity : AuditedEntity
{
    public Guid ApplicationRefId { get; set; }
    public string ProviderType { get; set; } = "OIDC";
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string JwksUri { get; set; } = string.Empty;
    public string[] AllowedAlgorithms { get; set; } = ["RS256"];
    public string[] RequiredScopes { get; set; } = [];
    public string RequiredClaims { get; set; } = "{}";
    public string ClaimMappings { get; set; } = "{}";
    public string SubjectType { get; set; } = "USER";
    public string SubjectClaim { get; set; } = "sub";
    public bool Enabled { get; set; } = true;
    public Guid VersionId { get; set; }
}

public sealed class RoleEntity : AuditedEntity
{
    public Guid ApplicationRefId { get; set; }
    public string RoleKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Privileged { get; set; }
    public string RiskLevel { get; set; } = "MEDIUM";
    public string Status { get; set; } = "ACTIVE";
}

public sealed class PermissionEntity : AuditedEntity
{
    public Guid ApplicationRefId { get; set; }
    public string PermissionKey { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string RiskLevel { get; set; } = "MEDIUM";
    public string Status { get; set; } = "ACTIVE";
}

public sealed class RolePermissionEntity : AuditedEntity
{
    public Guid ApplicationRefId { get; set; }
    public Guid RoleRefId { get; set; }
    public Guid PermissionRefId { get; set; }
    public Guid VersionId { get; set; }
    public string State { get; set; } = "DRAFT";
    public DateTimeOffset? ValidFrom { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}

public sealed class AssignmentEntity : AuditedEntity
{
    public Guid ApplicationRefId { get; set; }
    public string SubjectType { get; set; } = string.Empty;
    public string? SubjectEmail { get; set; }
    public string? GroupId { get; set; }
    public Guid RoleRefId { get; set; }
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string Source { get; set; } = "MANUAL";
    public string State { get; set; } = "ACTIVE";
    public string? Reason { get; set; }
}

public sealed class AssignmentAttributeEntity : AuditedEntity
{
    public Guid AssignmentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = "null";
    public string ValueType { get; set; } = string.Empty;
}

public sealed class PolicyEntity : AuditedEntity
{
    public Guid ApplicationRefId { get; set; }
    public string PolicyKey { get; set; } = string.Empty;
    public Guid PermissionRefId { get; set; }
    public string Effect { get; set; } = string.Empty;
    public string Conditions { get; set; } = "{}";

    /// <summary>
    /// Relative importance used by the <c>first-applicable</c> combining algorithm (higher wins)
    /// and as a deterministic tie-breaker for obligation ordering. Defaults to 0.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// JSON array of obligations returned to the caller when this policy contributes to the final
    /// decision — each entry is <c>{ "id": string, "value"?: string }</c>. Defaults to an empty
    /// array. Advisory instructions (e.g. <c>require_mfa</c>) the calling application must honor.
    /// </summary>
    public string Obligations { get; set; } = "[]";
    public Guid VersionId { get; set; }
    public string State { get; set; } = "DRAFT";
    public DateTimeOffset? PublishedAt { get; set; }
}

/// <summary>
/// A named, application-scoped reference-data document (a JSON list or object) that policy
/// conditions can reference via <c>reference.&lt;key&gt;</c> instead of hard-coding values. Maintained
/// once and reused across policies; soft-deleted via <see cref="Status"/> to preserve history.
/// </summary>
public sealed class ReferenceDataEntity : AuditedEntity
{
    public Guid ApplicationRefId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>The reference document as JSON (typically an array such as <c>["US","CA"]</c>).</summary>
    public string Value { get; set; } = "[]";
    public string Status { get; set; } = "ACTIVE";
}

/// <summary>
/// F7 — a Separation-of-Duties rule: a pair of permission matchers that must never be held together
/// (within a single role or across a subject's combined active assignments). Authored by admins;
/// the matcher pair may be drafted from natural language by the AI layer but is always validated
/// and persisted here as reviewable governance data.
/// </summary>
public sealed class SodRuleEntity : AuditedEntity
{
    public Guid ApplicationRefId { get; set; }
    public string RuleKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Rationale { get; set; }
    public string Severity { get; set; } = "HIGH";
    /// <summary>JSON permission matcher: { "permissionKey"?, "resource"?, "action"? } (at least one set).</summary>
    public string MatcherA { get; set; } = "{}";
    /// <summary>JSON permission matcher: { "permissionKey"?, "resource"?, "action"? } (at least one set).</summary>
    public string MatcherB { get; set; } = "{}";
    public string Status { get; set; } = "ACTIVE";
}

/// <summary>
/// A recurring access-review (recertification) campaign scoped to one application. Activating a
/// campaign snapshots the in-scope active assignments into <see cref="ReviewItemEntity"/> rows;
/// finalising applies the approved REVOKE outcomes through the normal audited revoke path.
/// </summary>
public sealed class ReviewCampaignEntity : AuditedEntity
{
    public Guid ApplicationRefId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>DRAFT (being set up) → ACTIVE (items generated, reviewers deciding) → CLOSED (finalised).</summary>
    public string Status { get; set; } = "DRAFT";
    public DateTimeOffset? DueAt { get; set; }
}

/// <summary>One reviewable access item within a campaign. Subject/role are denormalised at
/// generation time so the worklist is stable even if the underlying assignment later changes.</summary>
public sealed class ReviewItemEntity : AuditedEntity
{
    public Guid CampaignRefId { get; set; }
    public Guid AssignmentRefId { get; set; }
    public string SubjectEmail { get; set; } = string.Empty;
    public string RoleKey { get; set; } = string.Empty;
    /// <summary>PENDING → KEEP | REVOKE | NEEDS_INFO.</summary>
    public string Decision { get; set; } = "PENDING";
    public string? DecisionNote { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
}

public sealed class AuditEventEntity
{
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? ApplicationId { get; set; }
    public string? ActorEmail { get; set; }
    public string? ActorRole { get; set; }
    public string? ActorClientId { get; set; }
    public string? TargetSubjectEmail { get; set; }
    public IPAddress? SourceIp { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Reason { get; set; }
    public string? CorrelationId { get; set; }
}

public sealed class DecisionEntity
{
    public string DecisionId { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public string SubjectType { get; set; } = string.Empty;
    public string? SubjectEmail { get; set; }
    public string ResourceType { get; set; } = string.Empty;
    public string? ResourceId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? ContextSnapshot { get; set; }
    public bool Allowed { get; set; }
    public string? DenyReason { get; set; }
    public string[] MatchedRoles { get; set; } = [];
    public string[] MatchedPermissions { get; set; } = [];
    public string[] MatchedPolicies { get; set; } = [];
    /// <summary>JSON array of obligations attached to the decision — <c>[{ "id", "value"? }]</c>.</summary>
    public string Obligations { get; set; } = "[]";
    public string VersionsUsed { get; set; } = "{}";
    public DateTimeOffset Timestamp { get; set; }
    public string? CorrelationId { get; set; }
}

/// <summary>
/// One metadata row per AI model invocation. Deliberately stores no prompt or response content and
/// no reversible subject PII — only the operational shape of a call (feature, outcome, latency,
/// token usage) so platform admins can see how the advisory AI surface is being used.
/// </summary>
public sealed class AiInvocationEntity
{
    public Guid Id { get; set; }

    /// <summary>Feature key, e.g. <c>policyAuthoring</c> or <c>auditNarrative</c> (matches the config flags).</summary>
    public string Feature { get; set; } = string.Empty;

    public string? ActorEmail { get; set; }
    public string? ActorRole { get; set; }

    /// <summary>Owning application, or <c>null</c> for platform-scoped (cross-application) invocations.</summary>
    public string? ApplicationId { get; set; }

    public string Provider { get; set; } = string.Empty;
    public string? Model { get; set; }

    /// <summary>One of <c>Success</c>, <c>Timeout</c>, <c>Error</c>.</summary>
    public string Outcome { get; set; } = string.Empty;

    public int LatencyMs { get; set; }

    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }

    public DateTimeOffset Timestamp { get; set; }
    public string? CorrelationId { get; set; }
}

/// <summary>
/// One row per admin-submitted, free-text AI request (access search, SoD rule draft, policy
/// authoring) capturing the exact prompt, the terminal outcome — including validation failures the
/// metadata-only <see cref="AiInvocationEntity"/> cannot see — and how the model interpreted the
/// request. Unlike <see cref="AiInvocationEntity"/> this DELIBERATELY stores prompt text so admins
/// can review failed prompts and improve the underlying planner/rule logic. Capture is gated by
/// the <c>Ai:Logging:CapturePrompts</c> flag and reads are admin-gated (see D-0013).
/// </summary>
public sealed class AiPromptLogEntity
{
    public Guid Id { get; set; }

    /// <summary>Feature key, e.g. <c>accessSearch</c>, <c>sodAnalysis</c>, <c>policyAuthoring</c>.</summary>
    public string Feature { get; set; } = string.Empty;

    public string? ActorEmail { get; set; }
    public string? ActorRole { get; set; }

    /// <summary>Owning application, or <c>null</c> for platform-scoped (cross-application) requests.</summary>
    public string? ApplicationId { get; set; }

    /// <summary>The exact free-text the admin submitted (capped at the configured prompt length).</summary>
    public string PromptText { get; set; } = string.Empty;

    /// <summary>
    /// One of <c>Succeeded</c>, <c>Unmapped</c>, <c>ValidationFailed</c>, <c>InvalidInput</c>,
    /// <c>Timeout</c>, <c>ModelError</c>.
    /// </summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Operator-facing failure detail for a non-success outcome; <c>null</c> on success.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>How the model interpreted the prompt (planned spec / drafted matchers) as JSON, when available.</summary>
    public string? Interpretation { get; set; }

    public string Provider { get; set; } = string.Empty;
    public string? Model { get; set; }

    public DateTimeOffset Timestamp { get; set; }
    public string? CorrelationId { get; set; }
}
