using System.Security.Claims;

namespace Authorization.Api.Authentication;

public sealed record JwtTokenValidationResult(bool Succeeded, ClaimsPrincipal? Principal, string? ErrorCode)
{
    public static JwtTokenValidationResult Success(ClaimsPrincipal principal)
    {
        return new JwtTokenValidationResult(true, principal, null);
    }

    public static JwtTokenValidationResult Failure(string errorCode)
    {
        return new JwtTokenValidationResult(false, null, errorCode);
    }
}