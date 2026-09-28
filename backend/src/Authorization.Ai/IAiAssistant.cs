namespace Authorization.Ai;

/// <summary>
/// Abstraction over the AI provider. Kept provider-neutral so the concrete
/// implementation (Azure OpenAI, an OpenAI-compatible endpoint, or a deterministic
/// stub) can be swapped by configuration alone. AI output is always advisory:
/// it never participates in the authorization enforcement path.
/// </summary>
public interface IAiAssistant
{
    /// <summary>
    /// F1 — Translate a natural-language instruction into a draft policy condition
    /// tree. The result is a suggestion only and must be validated and accepted by a
    /// human before it is persisted.
    /// </summary>
    Task<PolicyDraftResult> DraftPolicyAsync(PolicyDraftRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// F2 — Produce a plain-language narrative that explains an authorization decision
    /// that has already been made by the deterministic policy engine.
    /// </summary>
    Task<DecisionExplanationResult> ExplainDecisionAsync(DecisionExplanationRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// F5 — Narrate the blast radius of publishing a draft policy. The counts and flips are
    /// computed deterministically by the API layer; the model only turns them into a concise,
    /// reviewer-friendly summary. Advisory only — it never gates the publish action.
    /// </summary>
    Task<ImpactNarrationResult> NarrateImpactAsync(ImpactNarrationRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// F6 — Turn a deterministic list of governance findings into a prioritized, explained
    /// backlog with a suggested fix per item. The findings themselves are authoritative and
    /// computed by the API layer; the model only ranks and explains. Advisory only.
    /// </summary>
    Task<ConfigAdvisorResult> SummarizeFindingsAsync(ConfigAdvisorRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// F8 — Translate a natural-language access question into a <b>closed, validated query spec</b>
    /// (entity + filters over a fixed field allow-list). The model is grounded in the app's real
    /// vocabulary and must never invent fields or emit SQL; the API layer validates and executes the
    /// spec with parameterized queries. Read-only.
    /// </summary>
    Task<AccessSearchPlanResult> PlanAccessSearchAsync(AccessSearchPlanRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// F7 — Draft a Separation-of-Duties rule from a natural-language instruction. The model returns a
    /// pair of permission matchers grounded in the application's real permission vocabulary (it may
    /// only reference permission keys / resources / actions that exist), plus a name, rationale, and
    /// severity. The result is a draft only: the API layer validates it and a human reviews it before
    /// it is persisted. The model never decides violations — detection is deterministic.
    /// </summary>
    Task<SodRuleDraftResult> DraftSodRuleAsync(SodRuleDraftRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// F4 — Summarize a subject's access for a recertification review. Every fact (roles, effective
    /// permissions, last-used signal, dormancy, peer comparison, and a deterministic keep/revoke/review
    /// recommendation) is computed by the API layer; the model only writes an overall summary and a
    /// one-line rationale per item, prioritizing the anomalies. It never changes a recommendation and
    /// never mutates state, and it receives <b>no subject PII</b> — items are keyed by pseudonymous ids.
    /// </summary>
    Task<AccessReviewSummaryResult> SummarizeAccessReviewAsync(AccessReviewSummaryRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// F9 — Narrate an audit change window into an auditor-ready compliance summary. Every fact (the
    /// events, grouped counts, and deny-reason aggregate) is computed deterministically by the API
    /// layer; the model only writes an overall summary and grouped sections, each citing concrete
    /// event ids it was given. It never invents an event, and it receives <b>no personal data</b> —
    /// actors and targets are referenced only by pseudonymous labels.
    /// </summary>
    Task<AuditNarrationResult> NarrateAuditAsync(AuditNarrationRequest request, CancellationToken cancellationToken);
}

/// <summary>Input for <see cref="IAiAssistant.DraftPolicyAsync"/>.</summary>
public sealed record PolicyDraftRequest(
    string ApplicationId,
    string Instruction,
    IReadOnlyList<string> KnownPermissionKeys,
    IReadOnlyList<string> KnownAttributes);

/// <summary>
/// A draft policy suggestion. <see cref="ConditionsJson"/> is a serialized condition
/// group in the same shape the visual condition builder consumes. <see cref="SuggestedEffect"/>
/// is the effect the model inferred from the instruction ("ALLOW" or "DENY"), or <c>null</c>
/// when the intent was not stated; it pre-selects the effect control but the author confirms it.
/// </summary>
public sealed record PolicyDraftResult(
    string ConditionsJson,
    string Summary,
    IReadOnlyList<string> Warnings,
    string? SuggestedEffect = null);

/// <summary>Input for <see cref="IAiAssistant.ExplainDecisionAsync"/>.</summary>
public sealed record DecisionExplanationRequest(
    string ApplicationId,
    bool Allowed,
    string? DenyReason,
    string SubjectType,
    string? SubjectEmail,
    string ResourceType,
    string? ResourceId,
    string Action,
    IReadOnlyList<string> MatchedRoles,
    IReadOnlyList<string> MatchedPermissions,
    IReadOnlyList<string> MatchedPolicies,
    DecisionDiagnostics? Diagnostics = null);

/// <summary>
/// Read-only facts gathered from the authorization store that let the assistant pinpoint the
/// exact cause of a denial (a misspelled email, a missing role grant, a blocking policy, …)
/// instead of guessing. Populated by the API layer for denied decisions; every field is
/// grounded in a database query so the narrative can cite concrete evidence.
/// </summary>
public sealed record DecisionDiagnostics(
    bool PermissionExists,
    bool ResourceTypeKnown,
    IReadOnlyList<string> AvailableActionsForResourceType,
    IReadOnlyList<string> KnownResourceTypes,
    bool SubjectKnown,
    int ActiveAssignmentCount,
    int RevokedAssignmentCount,
    int ExpiredAssignmentCount,
    int PendingAssignmentCount,
    IReadOnlyList<string> SubjectRoleKeys,
    IReadOnlyList<string> SimilarKnownSubjectEmails,
    IReadOnlyList<string> RolesGrantingPermission,
    IReadOnlyList<PolicyDiagnostic> RelevantPolicies,
    IReadOnlyList<string> ReferencedContextAttributes,
    IReadOnlyList<string> ProvidedContextKeys);

/// <summary>A policy relevant to the decision, with its raw conditions so the model can explain it.</summary>
public sealed record PolicyDiagnostic(
    string PolicyKey,
    string Effect,
    string ConditionsJson,
    bool Matched);

/// <summary>
/// A plain-language explanation plus zero or more ordered remediation steps. The steps are empty
/// when the decision was allowed.
/// </summary>
public sealed record DecisionExplanationResult(
    string Narrative,
    IReadOnlyList<string> Remediation);

/// <summary>
/// Input for <see cref="IAiAssistant.NarrateImpactAsync"/>. Every field is a deterministic fact
/// computed by shadow-evaluating the draft policy against representative requests; the model does
/// not query anything.
/// </summary>
public sealed record ImpactNarrationRequest(
    string ApplicationId,
    string PolicyKey,
    string Effect,
    int EvaluatedCount,
    int AllowToDenyCount,
    int DenyToAllowCount,
    bool SampledFromHistory,
    IReadOnlyList<ImpactFlipFact> Flips);

/// <summary>A single subject whose allow/deny outcome changes when the draft policy is published.</summary>
public sealed record ImpactFlipFact(
    string? SubjectEmail,
    string? ResourceId,
    string Action,
    bool Before,
    bool After,
    string? Reason);

/// <summary>A concise, reviewer-friendly narrative of the publish impact. Advisory only.</summary>
public sealed record ImpactNarrationResult(string Summary);

/// <summary>
/// Input for <see cref="IAiAssistant.SummarizeFindingsAsync"/>. The findings are computed
/// deterministically by the API layer's config advisor; the model does not query anything.
/// </summary>
public sealed record ConfigAdvisorRequest(
    string ApplicationId,
    IReadOnlyList<ConfigFindingFact> Findings);

/// <summary>A single deterministic governance finding the model should rank and explain.</summary>
public sealed record ConfigFindingFact(
    string Id,
    string Kind,
    string Severity,
    string Title,
    string Detail,
    string? EntityType,
    string? EntityKey);

/// <summary>
/// The model's advisory narration over the findings: a short overall summary plus a suggested
/// fix per finding (keyed by <see cref="ConfigFindingFact.Id"/>). Deterministic findings remain
/// the source of truth; missing suggestions simply have no fix text.
/// </summary>
public sealed record ConfigAdvisorResult(
    string Summary,
    IReadOnlyList<ConfigFindingSuggestion> Suggestions);

/// <summary>A suggested remediation for a single finding, referenced by its id.</summary>
public sealed record ConfigFindingSuggestion(
    string Id,
    string SuggestedFix);

/// <summary>
/// Input for <see cref="IAiAssistant.PlanAccessSearchAsync"/>. The model receives the closed schema
/// (allowed entities and their filterable fields) plus the application's real vocabulary so it can
/// ground values and never invent fields.
/// </summary>
public sealed record AccessSearchPlanRequest(
    string Question,
    IReadOnlyList<AccessSearchEntitySchema> Schema,
    AccessSearchVocabulary Vocabulary,
    string? Hierarchy = null);

/// <summary>One allowed entity and the fields a filter may reference for it.</summary>
public sealed record AccessSearchEntitySchema(
    string Entity,
    IReadOnlyList<string> Fields,
    IReadOnlyList<string>? Relationships = null,
    string? Description = null,
    IReadOnlyList<AccessSearchFieldSchema>? FieldSchemas = null);

/// <summary>
/// Rich, closed description of one filterable field so the model can reason about the right operator
/// and value. <see cref="Type"/> is one of <c>string</c>, <c>enum</c>, <c>bool</c>, <c>number</c>,
/// <c>date</c>, or <c>ref</c> (a join key grounded in the vocabulary). <see cref="Values"/> lists the
/// allowed enum/boolean domain (or a sample of grounded values); it is never authoritative for SQL —
/// the executor still validates every field/operator against the closed allow-list.
/// </summary>
public sealed record AccessSearchFieldSchema(
    string Name,
    string Type,
    IReadOnlyList<string>? Values = null,
    string? Description = null,
    IReadOnlyList<string>? Operators = null);


/// <summary>Real, grounded vocabulary from the application so the model uses only values that exist.</summary>
public sealed record AccessSearchVocabulary(
    IReadOnlyList<string> PermissionKeys,
    IReadOnlyList<string> Resources,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> RoleKeys,
    IReadOnlyList<string> ResourceIds);

/// <summary>
/// The model's planned query in the closed schema. <see cref="Entity"/> is empty when the question
/// could not be mapped to a searchable entity (the caller then returns a validation error).
/// </summary>
public sealed record AccessSearchPlanResult(
    string Entity,
    IReadOnlyList<AccessSearchPlanFilter> Filters,
    string? Explanation,
    string? Aggregate = null,
    string? GroupBy = null,
    string? Include = null,
    IReadOnlyList<string>? Suggestions = null,
    string? GroupByField = null,
    int? Limit = null,
    string? AbsentRelationship = null,
    string? IncludeChild = null,
    string? PresentRelationship = null,
    string? GroupBySecondaryField = null,
    string? IncludeGrandchild = null);

/// <summary>A single planned filter: field + operator (<c>eq</c>|<c>contains</c>) + value.</summary>
public sealed record AccessSearchPlanFilter(
    string Field,
    string Operator,
    string Value);

/// <summary>
/// Input for <see cref="IAiAssistant.DraftSodRuleAsync"/>. The model receives the application's real
/// permission vocabulary so the drafted matchers reference only values that exist.
/// </summary>
public sealed record SodRuleDraftRequest(
    string ApplicationId,
    string Instruction,
    IReadOnlyList<string> KnownPermissionKeys,
    IReadOnlyList<string> KnownResources,
    IReadOnlyList<string> KnownActions);

/// <summary>
/// A drafted Separation-of-Duties rule. Each matcher is grounded in the real vocabulary; the API
/// layer validates it before persisting. <see cref="MatcherA"/>/<see cref="MatcherB"/> are the two
/// sides of the toxic combination that must never be held together.
/// </summary>
public sealed record SodRuleDraftResult(
    string Name,
    string Rationale,
    string Severity,
    SodMatcherDraft MatcherA,
    SodMatcherDraft MatcherB,
    IReadOnlyList<string> Warnings);

/// <summary>One side of a drafted SoD rule: a permission key, or a resource/action pair.</summary>
public sealed record SodMatcherDraft(
    string? PermissionKey,
    string? Resource,
    string? Action);

/// <summary>
/// Input for <see cref="IAiAssistant.SummarizeAccessReviewAsync"/>. Carries only non-PII facts: the
/// subject is referenced by a pseudonymous <see cref="SubjectLabel"/> and each item by a stable id.
/// The deterministic recommendation is already decided; the model prioritizes and explains.
/// </summary>
public sealed record AccessReviewSummaryRequest(
    string SubjectLabel,
    int ItemCount,
    int KeepCount,
    int RevokeCount,
    int ReviewCount,
    IReadOnlyList<AccessReviewItemFact> Items);

/// <summary>
/// A single access grant under review, described with non-PII facts only. <see cref="Id"/> is a
/// stable identifier the model echoes back in its per-item rationale so the API can re-associate it.
/// </summary>
public sealed record AccessReviewItemFact(
    string Id,
    string ApplicationId,
    string RoleKey,
    string RoleName,
    bool Privileged,
    string RiskLevel,
    string Recommendation,
    string RecommendationReason,
    int? LastUsedDaysAgo,
    bool Dormant,
    int PeerCount,
    IReadOnlyList<string> Permissions);

/// <summary>
/// The model's advisory narration over the review: an overall summary plus a one-line rationale per
/// item (keyed by <see cref="AccessReviewItemFact.Id"/>). The deterministic recommendations remain the
/// source of truth; missing rationales simply have no explanatory text.
/// </summary>
public sealed record AccessReviewSummaryResult(
    string Summary,
    IReadOnlyList<AccessReviewItemRationale> Rationales);

/// <summary>A one-line explanation for a single reviewed item, referenced by its id.</summary>
public sealed record AccessReviewItemRationale(
    string Id,
    string Rationale);

/// <summary>
/// Input for <see cref="IAiAssistant.NarrateAuditAsync"/>. Carries only non-PII facts: actors and
/// targets are referenced by pseudonymous labels and each event by its opaque id. The events and
/// counts are already computed; the model groups, narrates, and cites them.
/// </summary>
public sealed record AuditNarrationRequest(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int TotalEvents,
    IReadOnlyList<AuditGroupFact> ByApplication,
    IReadOnlyList<AuditGroupFact> ByActor,
    IReadOnlyList<AuditGroupFact> ByEventType,
    IReadOnlyList<AuditDenyFact> TopDenyReasons,
    IReadOnlyList<AuditEventFact> Events);

/// <summary>A grouped count the model may reference (by application, pseudonymous actor, or type).</summary>
public sealed record AuditGroupFact(string Key, int Count);

/// <summary>A deny-reason aggregate for one application in the window.</summary>
public sealed record AuditDenyFact(string ApplicationId, string DenyReason, int Count);

/// <summary>
/// A single audit event described with non-PII facts only. <see cref="EventId"/> is the stable
/// citation the model echoes back in each section so the API can verify it against real events.
/// </summary>
public sealed record AuditEventFact(
    string EventId,
    string EventType,
    string? ApplicationId,
    string? ActorLabel,
    string? TargetLabel,
    DateTimeOffset Timestamp,
    string? Reason);

/// <summary>
/// The model's advisory narration over the window: an overall summary plus grouped sections, each
/// citing the concrete <see cref="AuditEventFact.EventId"/>s it covers. The deterministic events and
/// counts remain the source of truth; sections citing unknown ids are dropped by the caller.
/// </summary>
public sealed record AuditNarrationResult(
    string Summary,
    IReadOnlyList<AuditNarrativeSection> Sections);

/// <summary>A titled section of the narrative with the event ids it is grounded in.</summary>
public sealed record AuditNarrativeSection(
    string Heading,
    string Detail,
    IReadOnlyList<string> EventIds);

