using System.ComponentModel.DataAnnotations;
using Authorization.Infrastructure.RuntimeAuthorization;

namespace Authorization.Api.Contracts;

public sealed record AuthorizeApiRequest(
    [Required] string ApplicationId,
    [Required] SubjectDto Subject,
    IReadOnlyDictionary<string, object?>? Claims,
    [Required] ResourceDto Resource,
    [Required] string Action,
    IReadOnlyDictionary<string, object?>? Context);

public sealed record BatchAuthorizeApiRequest(
    [Required] string ApplicationId,
    [Required] SubjectDto Subject,
    IReadOnlyDictionary<string, object?>? Claims,
    IReadOnlyDictionary<string, object?>? Context,
    [Required] IReadOnlyList<BatchAuthorizeCheck> Checks);

public sealed record BatchAuthorizeCheck(
    [Required] ResourceDto Resource,
    [Required] string Action,
    IReadOnlyDictionary<string, object?>? Context);

public sealed record SubjectDto(
    [Required] string Type,
    string? Email);

public sealed record ResourceDto(
    [Required] string Type,
    string? Id);

public sealed record AuthorizeResponse(
    bool Allowed,
    string DecisionId,
    string? DenyReason,
    AuthorizeReasonResponse Reason,
    IReadOnlyCollection<AuthorizeObligationResponse> Obligations)
{
    public static AuthorizeResponse FromDecision(AuthorizeDecision decision)
    {
        return new AuthorizeResponse(
            decision.Allowed,
            decision.DecisionId,
            decision.DenyReason,
            new AuthorizeReasonResponse(decision.MatchedRoles, decision.MatchedPermissions, decision.MatchedPolicies),
            decision.Obligations.Select(obligation => new AuthorizeObligationResponse(obligation.Id, obligation.Value)).ToArray());
    }

    public static AuthorizeResponse Denied(string decisionId, string denyReason)
    {
        return new AuthorizeResponse(
            Allowed: false,
            decisionId,
            denyReason,
            new AuthorizeReasonResponse([], [], []),
            []);
    }
}

public sealed record AuthorizeReasonResponse(
    IReadOnlyCollection<string> MatchedRoles,
    IReadOnlyCollection<string> MatchedPermissions,
    IReadOnlyCollection<string> MatchedPolicies);

/// <summary>An advisory instruction the calling application must enforce (e.g. <c>require_mfa</c>).</summary>
public sealed record AuthorizeObligationResponse(string Id, string? Value);

public sealed record BatchAuthorizeResponse(IReadOnlyCollection<AuthorizeResponse> Results);
