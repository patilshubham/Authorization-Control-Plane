using System.Text.Json;
using Authorization.Api.Authentication;
using Authorization.Api.Contracts;
using Authorization.Api.Errors;
using Authorization.Infrastructure.RuntimeAuthorization;
using Microsoft.AspNetCore.Mvc;

namespace Authorization.Api.Controllers;

[ApiController]
[Route("v1")]
public sealed class RuntimeAuthorizationController : ControllerBase
{
    public const int MaxBatchSize = 50;
    public const long MaxRequestBodyBytes = 256 * 1024;
    public const int MaxContextJsonBytes = 32 * 1024;
    public const string SubjectMismatch = "SUBJECT_MISMATCH";
    public const string BatchTooLarge = "BATCH_TOO_LARGE";
    public const string RequestTooLarge = "REQUEST_TOO_LARGE";
    public const string ContextTooLarge = "CONTEXT_TOO_LARGE";

    private readonly RuntimeCallerAuthenticator callerAuthenticator;
    private readonly IAuthorizationPolicyEngine policyEngine;
    private readonly IDecisionRecorder decisionRecorder;

    public RuntimeAuthorizationController(
        RuntimeCallerAuthenticator callerAuthenticator,
        IAuthorizationPolicyEngine policyEngine,
        IDecisionRecorder decisionRecorder)
    {
        this.callerAuthenticator = callerAuthenticator;
        this.policyEngine = policyEngine;
        this.decisionRecorder = decisionRecorder;
    }

    [HttpPost("authorize")]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [ProducesResponseType(typeof(AuthorizeResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> AuthorizeAsync([FromBody] AuthorizeApiRequest request, CancellationToken cancellationToken)
    {
        IActionResult? limitFailure = ValidateRequestLimits(request.Context);
        if (limitFailure is not null)
        {
            return limitFailure;
        }

        RuntimeClientValidationResult caller = await callerAuthenticator.AuthenticateAsync(ExtractBearerToken(), request.ApplicationId, cancellationToken);
        if (!caller.Succeeded)
        {
            return CallerFailure(caller);
        }

        IActionResult? subjectFailure = ResolveSubject(request.Subject, caller, out string subjectType, out string subjectEmail);
        if (subjectFailure is not null)
        {
            return subjectFailure;
        }

        AuthorizeRequest engineRequest = ToEngineRequest(request, subjectType, subjectEmail);
        AuthorizeDecision decision = await policyEngine.AuthorizeAsync(engineRequest, cancellationToken);
        await decisionRecorder.RecordAsync(ToDecisionRecord(engineRequest, decision), cancellationToken);
        return Ok(AuthorizeResponse.FromDecision(decision));
    }

    [HttpPost("authorize/batch")]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [ProducesResponseType(typeof(BatchAuthorizeResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> AuthorizeBatchAsync([FromBody] BatchAuthorizeApiRequest request, CancellationToken cancellationToken)
    {
        if (request.Checks.Count > MaxBatchSize)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, Error(BatchTooLarge, $"Batch requests are limited to {MaxBatchSize} checks."));
        }

        IActionResult? requestLimitFailure = ValidateRequestLimits(request.Context);
        if (requestLimitFailure is not null)
        {
            return requestLimitFailure;
        }

        RuntimeClientValidationResult caller = await callerAuthenticator.AuthenticateAsync(ExtractBearerToken(), request.ApplicationId, cancellationToken);
        if (!caller.Succeeded)
        {
            return CallerFailure(caller);
        }

        IActionResult? subjectFailure = ResolveSubject(request.Subject, caller, out string subjectType, out string subjectEmail);
        if (subjectFailure is not null)
        {
            return subjectFailure;
        }

        var results = new List<AuthorizeResponse>(request.Checks.Count);
        foreach (BatchAuthorizeCheck check in request.Checks)
        {
            IActionResult? checkLimitFailure = ValidateContextSize(check.Context);
            if (checkLimitFailure is ObjectResult objectResult && objectResult.Value is ApiErrorEnvelope envelope)
            {
                results.Add(AuthorizeResponse.Denied(Guid.NewGuid().ToString("N"), envelope.Error.Code));
                continue;
            }

            AuthorizeRequest engineRequest = ToEngineRequest(request, check, subjectType, subjectEmail);
            AuthorizeDecision decision = await policyEngine.AuthorizeAsync(engineRequest, cancellationToken);
            await decisionRecorder.RecordAsync(ToDecisionRecord(engineRequest, decision), cancellationToken);
            results.Add(AuthorizeResponse.FromDecision(decision));
        }

        return Ok(new BatchAuthorizeResponse(results));
    }

    private IActionResult? ValidateRequestLimits(IReadOnlyDictionary<string, object?>? context)
    {
        if (Request.ContentLength > MaxRequestBodyBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, Error(RequestTooLarge, $"Request body must be {MaxRequestBodyBytes} bytes or smaller."));
        }

        return ValidateContextSize(context);
    }

    private IActionResult? ValidateContextSize(IReadOnlyDictionary<string, object?>? context)
    {
        if (context is null)
        {
            return null;
        }

        int contextBytes = JsonSerializer.SerializeToUtf8Bytes(context).Length;
        return contextBytes > MaxContextJsonBytes
            ? UnprocessableEntity(Error(ContextTooLarge, $"Context JSON must be {MaxContextJsonBytes} bytes or smaller."))
            : null;
    }

    private IActionResult CallerFailure(RuntimeClientValidationResult validationResult)
    {
        int statusCode = validationResult.ErrorCode is RuntimeCallerAuthenticator.CallerApplicationMismatch or RuntimeCallerAuthenticator.CallerSubjectClaimMissing
            ? StatusCodes.Status403Forbidden
            : StatusCodes.Status401Unauthorized;

        return StatusCode(statusCode, Error(validationResult.ErrorCode ?? RuntimeCallerAuthenticator.CallerUnauthenticated, "Runtime caller credentials are invalid for the requested application."));
    }

    // The subject is derived authoritatively from the verified token (per the matched provider's
    // configured claim). A subject supplied in the request body is treated as untrusted: it may only
    // corroborate the token, never override it. Any conflicting body subject is rejected (403).
    private IActionResult? ResolveSubject(SubjectDto? bodySubject, RuntimeClientValidationResult caller, out string subjectType, out string subjectEmail)
    {
        subjectType = caller.SubjectType!;
        subjectEmail = caller.SubjectIdentifier!;

        string? bodyEmail = bodySubject?.Email;
        if (!string.IsNullOrWhiteSpace(bodyEmail) && !string.Equals(bodyEmail, subjectEmail, StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, Error(SubjectMismatch, "The subject in the request does not match the authenticated token subject."));
        }

        return null;
    }

    private string? ExtractBearerToken()
    {
        string? header = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }

    private ApiErrorEnvelope Error(string code, string message)
    {
        return new ApiErrorEnvelope(new ApiError(code, message), HttpContext.TraceIdentifier);
    }

    private static AuthorizeRequest ToEngineRequest(AuthorizeApiRequest request, string subjectType, string subjectEmail)
    {
        return new AuthorizeRequest(
            request.ApplicationId,
            subjectType,
            subjectEmail,
            request.Resource.Type,
            request.Resource.Id,
            request.Action,
            request.Context ?? new Dictionary<string, object?>());
    }

    private static AuthorizeRequest ToEngineRequest(BatchAuthorizeApiRequest request, BatchAuthorizeCheck check, string subjectType, string subjectEmail)
    {
        return new AuthorizeRequest(
            request.ApplicationId,
            subjectType,
            subjectEmail,
            check.Resource.Type,
            check.Resource.Id,
            check.Action,
            MergeContext(request.Context, check.Context));
    }

    private static IReadOnlyDictionary<string, object?> MergeContext(IReadOnlyDictionary<string, object?>? requestContext, IReadOnlyDictionary<string, object?>? checkContext)
    {
        var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (requestContext is not null)
        {
            foreach (KeyValuePair<string, object?> item in requestContext)
            {
                merged[item.Key] = item.Value;
            }
        }

        if (checkContext is not null)
        {
            foreach (KeyValuePair<string, object?> item in checkContext)
            {
                merged[item.Key] = item.Value;
            }
        }

        return merged;
    }

    private DecisionRecord ToDecisionRecord(AuthorizeRequest request, AuthorizeDecision decision)
    {
        return new DecisionRecord(
            decision.DecisionId,
            request.ApplicationId,
            request.SubjectType,
            request.SubjectEmail,
            request.ResourceType,
            request.ResourceId,
            request.Action,
            request.Context,
            decision.Allowed,
            decision.DenyReason,
            decision.MatchedRoles,
            decision.MatchedPermissions,
            decision.MatchedPolicies,
            decision.Obligations,
            DateTimeOffset.UtcNow,
            HttpContext.TraceIdentifier);
    }
}
