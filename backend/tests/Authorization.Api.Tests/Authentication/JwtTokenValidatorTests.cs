using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Authorization.Api.Authentication;
using Microsoft.IdentityModel.Tokens;

namespace Authorization.Api.Tests.Authentication;

public sealed class JwtTokenValidatorTests
{
    private const string Issuer = "http://keycloak:8080/realms/authorization-local";
    private const string Audience = "authorization-api";
    private readonly SymmetricSecurityKey signingKey = new(Encoding.UTF8.GetBytes("local-test-signing-key-minimum-32-bytes"));
    private readonly JwtTokenValidator validator = new();

    [Fact]
    public void Validate_AcceptsValidSignedToken()
    {
        string token = CreateToken(Issuer, Audience, signingKey);

        JwtTokenValidationResult result = validator.Validate(token, CreateValidationParameters(signingKey));

        Assert.True(result.Succeeded);
        Assert.Equal("admin@local", result.Principal!.FindFirstValue("email"));
    }

    [Fact]
    public void Validate_RejectsMissingToken()
    {
        JwtTokenValidationResult result = validator.Validate(null, CreateValidationParameters(signingKey));

        Assert.False(result.Succeeded);
        Assert.Equal(JwtTokenValidator.MissingToken, result.ErrorCode);
    }

    [Fact]
    public void Validate_RejectsInvalidIssuer()
    {
        string token = CreateToken("http://issuer.example.invalid", Audience, signingKey);

        JwtTokenValidationResult result = validator.Validate(token, CreateValidationParameters(signingKey));

        Assert.False(result.Succeeded);
        Assert.Equal(JwtTokenValidator.InvalidIssuer, result.ErrorCode);
    }

    [Fact]
    public void Validate_RejectsInvalidAudience()
    {
        string token = CreateToken(Issuer, "wrong-audience", signingKey);

        JwtTokenValidationResult result = validator.Validate(token, CreateValidationParameters(signingKey));

        Assert.False(result.Succeeded);
        Assert.Equal(JwtTokenValidator.InvalidAudience, result.ErrorCode);
    }

    [Fact]
    public void Validate_RejectsInvalidSignature()
    {
        string token = CreateToken(Issuer, Audience, new SymmetricSecurityKey(Encoding.UTF8.GetBytes("wrong-test-signing-key-minimum-32-bytes")));

        JwtTokenValidationResult result = validator.Validate(token, CreateValidationParameters(signingKey));

        Assert.False(result.Succeeded);
        Assert.Equal(JwtTokenValidator.InvalidSignature, result.ErrorCode);
    }

    [Fact]
    public void Validate_RejectsAlgNoneToken()
    {
        var token = new JwtSecurityToken(
            Issuer,
            Audience,
            CreateClaims(),
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5),
            signingCredentials: null);

        JwtTokenValidationResult result = validator.Validate(new JwtSecurityTokenHandler().WriteToken(token), CreateValidationParameters(signingKey));

        Assert.False(result.Succeeded);
        Assert.Equal(JwtTokenValidator.InvalidAlgorithm, result.ErrorCode);
    }

    private static string CreateToken(string issuer, string audience, SecurityKey securityKey)
    {
        var token = new JwtSecurityToken(
            issuer,
            audience,
            CreateClaims(),
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static Claim[] CreateClaims()
    {
        return [new Claim("email", "admin@local")];
    }

    private static TokenValidationParameters CreateValidationParameters(SecurityKey securityKey)
    {
        return new TokenValidationParameters
        {
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = securityKey,
            ValidateLifetime = true,
            RequireSignedTokens = true,
        };
    }
}