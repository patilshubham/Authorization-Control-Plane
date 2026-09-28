using System.Security.Claims;
using System.Text.Json;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Authorization.Api.Authentication;

/// <summary>
/// Authenticates a runtime caller by validating the bearer token it presents against the OIDC
/// provider(s) registered for the requested application. The token proves both authenticity
/// (signature/issuer/audience) and binding to the application (required claims such as <c>azp</c>).
/// </summary>
public sealed class RuntimeCallerAuthenticator
{
    public const string CallerUnauthenticated = "CALLER_UNAUTHENTICATED";
    public const string CallerApplicationMismatch = "CALLER_APPLICATION_MISMATCH";
    public const string CallerSubjectClaimMissing = "CALLER_SUBJECT_CLAIM_MISSING";

    private readonly AuthorizationDbContext dbContext;
    private readonly JwtTokenValidator tokenValidator;
    private readonly IRuntimeSigningKeyResolver signingKeyResolver;

    public RuntimeCallerAuthenticator(
        AuthorizationDbContext dbContext,
        JwtTokenValidator tokenValidator,
        IRuntimeSigningKeyResolver signingKeyResolver)
    {
        this.dbContext = dbContext;
        this.tokenValidator = tokenValidator;
        this.signingKeyResolver = signingKeyResolver;
    }

    public async Task<RuntimeClientValidationResult> AuthenticateAsync(
        string? bearerToken,
        string requestedApplicationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            return RuntimeClientValidationResult.Failure(CallerUnauthenticated);
        }

        List<OidcProviderEntity> providers = await dbContext.OidcProviders
            .AsNoTracking()
            .Where(provider => provider.Enabled)
            .Join(
                dbContext.Applications.AsNoTracking(),
                provider => provider.ApplicationRefId,
                application => application.Id,
                (provider, application) => new { Provider = provider, application.ApplicationId })
            .Where(joined => joined.ApplicationId == requestedApplicationId)
            .Select(joined => joined.Provider)
            .ToListAsync(cancellationToken);

        if (providers.Count == 0)
        {
            return RuntimeClientValidationResult.Failure(CallerUnauthenticated);
        }

        bool tokenIsAuthentic = false;
        bool matchedButSubjectMissing = false;
        foreach (OidcProviderEntity provider in providers)
        {
            IReadOnlyCollection<SecurityKey> signingKeys;
            try
            {
                signingKeys = await signingKeyResolver.ResolveAsync(provider.JwksUri, cancellationToken);
            }
            catch (HttpRequestException)
            {
                continue;
            }

            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = provider.Issuer,
                ValidateAudience = true,
                ValidAudience = provider.Audience,
                ValidAlgorithms = provider.AllowedAlgorithms,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = signingKeys,
                ValidateLifetime = true,
                RequireSignedTokens = true,
            };

            JwtTokenValidationResult validation = tokenValidator.Validate(bearerToken, validationParameters);
            if (!validation.Succeeded || validation.Principal is null)
            {
                continue;
            }

            tokenIsAuthentic = true;
            if (!RequiredClaimsSatisfied(provider.RequiredClaims, validation.Principal))
            {
                continue;
            }

            // The provider's registered authentication type dictates how the subject is derived:
            // a SERVICE_ACCOUNT provider reads the client-id claim (e.g. azp); a USER provider reads
            // the user-identity claim (e.g. sub/preferred_username). The subject is taken from the
            // verified token — never from untrusted request input.
            string? subjectIdentifier = ExtractSubjectClaim(provider.SubjectClaim, validation.Principal);
            if (string.IsNullOrWhiteSpace(subjectIdentifier))
            {
                matchedButSubjectMissing = true;
                continue;
            }

            return RuntimeClientValidationResult.Success(requestedApplicationId, provider.SubjectType, subjectIdentifier);
        }

        // A token whose app binding matched but which lacks the configured subject claim is the wrong
        // token type for this provider (403); a token that validated against the issuer but failed the
        // required-claim binding is authentic yet not authorized for the application (403); a token that
        // never validated cannot be authenticated at all (401).
        if (matchedButSubjectMissing)
        {
            return RuntimeClientValidationResult.Failure(CallerSubjectClaimMissing);
        }

        return tokenIsAuthentic
            ? RuntimeClientValidationResult.Failure(CallerApplicationMismatch)
            : RuntimeClientValidationResult.Failure(CallerUnauthenticated);
    }

    private static string? ExtractSubjectClaim(string subjectClaim, ClaimsPrincipal principal)
    {
        string claimKey = string.IsNullOrWhiteSpace(subjectClaim) ? "sub" : subjectClaim.Trim();
        return principal.Claims.FirstOrDefault(claim => claim.Type == claimKey)?.Value;
    }

    private static bool RequiredClaimsSatisfied(string requiredClaimsJson, ClaimsPrincipal principal)
    {
        if (string.IsNullOrWhiteSpace(requiredClaimsJson) || requiredClaimsJson == "{}")
        {
            return true;
        }

        Dictionary<string, string>? requiredClaims;
        try
        {
            requiredClaims = JsonSerializer.Deserialize<Dictionary<string, string>>(requiredClaimsJson);
        }
        catch (JsonException)
        {
            return false;
        }

        if (requiredClaims is null || requiredClaims.Count == 0)
        {
            return true;
        }

        return requiredClaims.All(required => principal.Claims.Any(claim =>
            claim.Type == required.Key && string.Equals(claim.Value, required.Value, StringComparison.Ordinal)));
    }
}
