using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;

namespace Authorization.Api.Authentication;

public sealed class JwtTokenValidator
{
    public const string MissingToken = "TOKEN_MISSING";
    public const string InvalidAlgorithm = "TOKEN_ALGORITHM_INVALID";
    public const string InvalidAudience = "TOKEN_AUDIENCE_INVALID";
    public const string InvalidIssuer = "TOKEN_ISSUER_INVALID";
    public const string InvalidSignature = "TOKEN_SIGNATURE_INVALID";
    public const string InvalidToken = "TOKEN_INVALID";

    private readonly JwtSecurityTokenHandler handler = new() { MapInboundClaims = false };

    public JwtTokenValidationResult Validate(string? token, TokenValidationParameters validationParameters)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return JwtTokenValidationResult.Failure(MissingToken);
        }

        try
        {
            if (handler.ReadJwtToken(token).Header.Alg.Equals(SecurityAlgorithms.None, StringComparison.OrdinalIgnoreCase))
            {
                return JwtTokenValidationResult.Failure(InvalidAlgorithm);
            }

            var principal = handler.ValidateToken(token, validationParameters, out _);
            return JwtTokenValidationResult.Success(principal);
        }
        catch (SecurityTokenInvalidAudienceException)
        {
            return JwtTokenValidationResult.Failure(InvalidAudience);
        }
        catch (SecurityTokenInvalidIssuerException)
        {
            return JwtTokenValidationResult.Failure(InvalidIssuer);
        }
        catch (SecurityTokenInvalidSignatureException)
        {
            return JwtTokenValidationResult.Failure(InvalidSignature);
        }
        catch (SecurityTokenException)
        {
            return JwtTokenValidationResult.Failure(InvalidToken);
        }
        catch (ArgumentException)
        {
            return JwtTokenValidationResult.Failure(InvalidToken);
        }
    }
}