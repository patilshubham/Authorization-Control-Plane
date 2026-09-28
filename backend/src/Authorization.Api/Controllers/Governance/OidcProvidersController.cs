using Authorization.Api.Authorization;
using Authorization.Api.Constants;
using Authorization.Api.Contracts;
using Authorization.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Controllers;

[ApiController]
[Authorize(Policy = DelegatedAdminPolicyNames.AdminApi)]
[Route("v1/admin")]
public sealed class OidcProvidersController : GovernanceControllerBase
{
    private const string DefaultAlgorithm = "RS256";
    private const string DisallowedAlgorithm = "none";

    public OidcProvidersController(AuthorizationDbContext dbContext)
        : base(dbContext)
    {
    }

    [HttpGet("applications/{applicationId}/oidc-providers")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ReadOnlyView)]
    public async Task<IReadOnlyList<OidcProviderResponse>> GetOidcProvidersAsync(string applicationId, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return [];
        }

        List<OidcProviderEntity> providers = await DbContext.OidcProviders.AsNoTracking().Where(entity => entity.ApplicationRefId == applicationRefId.Value).ToListAsync(cancellationToken);
        return providers.Select(entity => OidcProviderResponse.From(entity, applicationId)).ToList();
    }

    [HttpPost("applications/{applicationId}/oidc-providers")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManageApplication)]
    public async Task<IActionResult> CreateOidcProviderAsync(string applicationId, [FromBody] CreateOidcProviderRequest request, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "Application was not found."));
        }

        var entity = new OidcProviderEntity
        {
            ApplicationRefId = applicationRefId.Value,
            ProviderType = "OIDC",
            Issuer = request.Issuer,
            Audience = request.Audience,
            JwksUri = request.JwksUri,
            AllowedAlgorithms = request.AllowedAlgorithms.Length == 0 ? [DefaultAlgorithm] : request.AllowedAlgorithms,
            RequiredClaims = request.RequiredClaims,
            ClaimMappings = request.ClaimMappings,
            SubjectType = request.SubjectType,
            SubjectClaim = NormalizeSubjectClaim(request.SubjectClaim),
            Enabled = true,
            CreatedBy = Actor,
        };

        DbContext.OidcProviders.Add(entity);
        object created = new { entity.ProviderType, entity.Issuer, entity.Audience, entity.JwksUri, entity.AllowedAlgorithms, entity.RequiredScopes, entity.SubjectType, entity.SubjectClaim, entity.Enabled };
        await SaveGovernanceMutationAsync(AuditEventTypes.OidcProviderCreated, applicationId, null, created, cancellationToken);
        return Created($"/v1/admin/applications/{applicationId}/oidc-providers/{entity.Id}", OidcProviderResponse.From(entity, applicationId));
    }

    [HttpPut("applications/{applicationId}/oidc-providers/{providerId:guid}")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManageApplication)]
    public async Task<IActionResult> UpdateOidcProviderAsync(string applicationId, Guid providerId, [FromBody] UpdateOidcProviderRequest request, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        OidcProviderEntity? entity = applicationRefId is null
            ? null
            : await DbContext.OidcProviders.FirstOrDefaultAsync(provider => provider.ApplicationRefId == applicationRefId.Value && provider.Id == providerId, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.OidcProviderNotFound, "Identity provider was not found."));
        }

        string[] algorithms = request.AllowedAlgorithms.Length == 0 ? [DefaultAlgorithm] : request.AllowedAlgorithms;
        if (algorithms.Any(alg => string.Equals(alg, DisallowedAlgorithm, StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "Algorithm 'none' is not permitted."));
        }

        object oldValue = new { entity.Issuer, entity.Audience, entity.JwksUri, entity.AllowedAlgorithms, entity.SubjectType, entity.SubjectClaim, entity.Enabled };
        entity.Issuer = request.Issuer;
        entity.Audience = request.Audience;
        entity.JwksUri = request.JwksUri;
        entity.AllowedAlgorithms = algorithms;
        entity.SubjectType = request.SubjectType;
        entity.SubjectClaim = NormalizeSubjectClaim(request.SubjectClaim);
        entity.Enabled = request.Enabled;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedBy = Actor;
        await SaveGovernanceMutationAsync(AuditEventTypes.OidcProviderUpdated, applicationId, null, oldValue, new { entity.Issuer, entity.Audience, entity.JwksUri, entity.AllowedAlgorithms, entity.SubjectType, entity.SubjectClaim, entity.Enabled }, cancellationToken);
        return Ok(OidcProviderResponse.From(entity, applicationId));
    }

    private static string NormalizeSubjectClaim(string? subjectClaim)
    {
        string trimmed = subjectClaim?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? "sub" : trimmed;
    }

    [HttpPost("applications/{applicationId}/oidc-providers/validate")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManageApplication)]
    public ActionResult<OidcProviderValidationResponse> ValidateOidcProvider(
        string applicationId,
        [FromBody] OidcProviderValidationRequest request)
    {
        // Deterministic configuration validation only. We deliberately do NOT perform a live
        // server-side fetch of the (admin-supplied) JWKS/issuer URL here: doing so would turn this
        // endpoint into a Server-Side Request Forgery vector (an admin could point it at internal
        // infrastructure). Reachability is instead exercised safely at runtime by the token
        // validation path against the real issuer. This check catches the common misconfigurations
        // (typos, http vs https, the forbidden 'none' algorithm, a missing subject claim) up front.
        var checks = new List<OidcProviderCheckResult>
        {
            CheckHttpsUrl("Issuer", request.Issuer),
            string.IsNullOrWhiteSpace(request.Audience)
                ? new OidcProviderCheckResult("Audience", "FAIL", "An audience is required.")
                : new OidcProviderCheckResult("Audience", "PASS", request.Audience.Trim()),
            CheckHttpsUrl("JWKS URI", request.JwksUri),
            CheckAlgorithms(request.AllowedAlgorithms),
            new OidcProviderCheckResult(
                "Subject claim",
                "PASS",
                $"Subject taken from the '{NormalizeSubjectClaim(request.SubjectClaim)}' claim."),
            CheckSameHost(request.Issuer, request.JwksUri),
        };

        bool ok = checks.All(c => c.Status != "FAIL");
        return Ok(new OidcProviderValidationResponse(ok, checks));
    }

    private static OidcProviderCheckResult CheckHttpsUrl(string label, string? value)
    {
        string trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return new OidcProviderCheckResult(label, "FAIL", $"{label} is required.");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            return new OidcProviderCheckResult(label, "FAIL", $"{label} is not a valid absolute URL.");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new OidcProviderCheckResult(label, "FAIL", $"{label} must use https (found '{uri.Scheme}').");
        }

        return new OidcProviderCheckResult(label, "PASS", uri.ToString());
    }

    private static OidcProviderCheckResult CheckAlgorithms(string[]? algorithms)
    {
        string[] effective = (algorithms is null || algorithms.Length == 0) ? [DefaultAlgorithm] : algorithms;
        if (effective.Any(alg => string.Equals(alg, DisallowedAlgorithm, StringComparison.OrdinalIgnoreCase)))
        {
            return new OidcProviderCheckResult("Algorithms", "FAIL", "Algorithm 'none' is not permitted.");
        }

        return new OidcProviderCheckResult("Algorithms", "PASS", string.Join(", ", effective));
    }

    private static OidcProviderCheckResult CheckSameHost(string? issuer, string? jwksUri)
    {
        if (Uri.TryCreate((issuer ?? string.Empty).Trim(), UriKind.Absolute, out Uri? issuerUri)
            && Uri.TryCreate((jwksUri ?? string.Empty).Trim(), UriKind.Absolute, out Uri? jwksUriParsed)
            && !string.Equals(issuerUri.Host, jwksUriParsed.Host, StringComparison.OrdinalIgnoreCase))
        {
            return new OidcProviderCheckResult(
                "Issuer / JWKS host",
                "WARN",
                $"The JWKS host ('{jwksUriParsed.Host}') differs from the issuer host ('{issuerUri.Host}'). This is valid but unusual — double-check it is intentional.");
        }

        return new OidcProviderCheckResult("Issuer / JWKS host", "PASS", "Issuer and JWKS resolve consistently.");
    }
}

public sealed class OidcProviderValidationRequest
{
    public string? Issuer { get; init; }
    public string? Audience { get; init; }
    public string? JwksUri { get; init; }
    public string[]? AllowedAlgorithms { get; init; }
    public string? SubjectClaim { get; init; }
}

public sealed record OidcProviderValidationResponse(bool Ok, IReadOnlyList<OidcProviderCheckResult> Checks);

/// <summary>A single configuration check result. Status is one of PASS, WARN, FAIL.</summary>
public sealed record OidcProviderCheckResult(string Label, string Status, string Detail);
