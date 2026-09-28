namespace Authorization.Infrastructure.RuntimeAuthorization;

public sealed record AuthorizeRequest(
    string ApplicationId,
    string SubjectType,
    string? SubjectEmail,
    string ResourceType,
    string? ResourceId,
    string Action,
    IReadOnlyDictionary<string, object?> Context);

public sealed record AuthorizeDecision(
    string DecisionId,
    bool Allowed,
    string? DenyReason,
    IReadOnlyCollection<string> MatchedRoles,
    IReadOnlyCollection<string> MatchedPermissions,
    IReadOnlyCollection<string> MatchedPolicies,
    IReadOnlyCollection<AuthorizeObligation> Obligations);

/// <summary>
/// An advisory instruction attached to a decision by a contributing policy (e.g.
/// <c>require_mfa</c>, <c>mask_ssn</c>). The engine surfaces obligations from the policies whose
/// effect matched the final decision; the calling application is responsible for enforcing them.
/// </summary>
public sealed record AuthorizeObligation(string Id, string? Value);

public interface IAuthorizationPolicyEngine
{
    Task<AuthorizeDecision> AuthorizeAsync(AuthorizeRequest request, CancellationToken cancellationToken = default);
}

public sealed record DecisionRecord(
    string DecisionId,
    string ApplicationId,
    string SubjectType,
    string? SubjectEmail,
    string ResourceType,
    string? ResourceId,
    string Action,
    IReadOnlyDictionary<string, object?> Context,
    bool Allowed,
    string? DenyReason,
    IReadOnlyCollection<string> MatchedRoles,
    IReadOnlyCollection<string> MatchedPermissions,
    IReadOnlyCollection<string> MatchedPolicies,
    IReadOnlyCollection<AuthorizeObligation> Obligations,
    DateTimeOffset Timestamp,
    string? CorrelationId);

public interface IDecisionRecorder
{
    ValueTask RecordAsync(DecisionRecord record, CancellationToken cancellationToken = default);
}
