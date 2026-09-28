using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Authorization.Api.Authentication;
using Authorization.Api.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Authorization.Infrastructure.Persistence;
using Microsoft.IdentityModel.Tokens;

namespace Authorization.Api.Tests;

public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private const string TestIssuer = "test-admin-issuer";
    private const string TestAudience = "authorization-api";
    private static readonly SymmetricSecurityKey TestSigningKey = new(Encoding.UTF8.GetBytes("test-admin-signing-key-minimum-32-bytes"));

    // Runtime callers present RS256 tokens matching the seeded OIDC provider records
    // (issuer/audience below). The test signing-key resolver returns this key instead of
    // fetching JWKS from Keycloak.
    private const string RuntimeIssuer = "http://keycloak:8080/realms/authorization-local";
    private const string RuntimeAudience = "authorization-api";
    private static readonly RSA RuntimeRsa = RSA.Create(2048);
    private static readonly RsaSecurityKey RuntimeSigningKey = new(RuntimeRsa) { KeyId = "test-runtime-key" };

    private readonly string databaseName = $"api-tests-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(configurationBuilder =>
        {
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:RunDevelopmentSetup"] = "false",
                // The seeder derives the runtime OIDC provider's issuer/audience from these settings.
                // Align them with the tokens the tests mint (RuntimeIssuer/RuntimeAudience) so the
                // seeded provider record accepts them.
                ["Oidc:Authority"] = RuntimeIssuer,
                ["Oidc:Audience"] = RuntimeAudience,
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AuthorizationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AuthorizationDbContext>>();
            services.RemoveAll<IRuntimeSigningKeyResolver>();
            services.AddDbContext<AuthorizationDbContext>(options =>
                options.UseInMemoryDatabase(databaseName));
            services.AddSingleton<IRuntimeSigningKeyResolver>(new TestRuntimeSigningKeyResolver(RuntimeSigningKey));
            services.PostConfigure<JwtBearerOptions>(ApiAuthenticationSchemes.AdminJwt, options =>
            {
                options.Authority = null!;
                options.MetadataAddress = null!;
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateAudience = true,
                    ValidAudience = TestAudience,
                    ValidateIssuer = true,
                    ValidIssuer = TestIssuer,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = TestSigningKey,
                    ValidateLifetime = true,
                    RequireSignedTokens = true,
                    RoleClaimType = DelegatedAdminClaimTypes.PlatformRole,
                };
            });
        });
    }

    public HttpClient CreateAdminClient()
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateAdminJwt(new Claim(DelegatedAdminClaimTypes.PlatformRole, DelegatedAdminRoles.PlatformSuperAdmin)));
        return client;
    }

    /// <summary>Admin client bound to a host derived via <see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/>.</summary>
    public HttpClient CreateAdminClient(WebApplicationFactory<Program> host)
    {
        HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateAdminJwt(new Claim(DelegatedAdminClaimTypes.PlatformRole, DelegatedAdminRoles.PlatformSuperAdmin)));
        return client;
    }

    /// <summary>Application-role client bound to a host derived via <see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/>.</summary>
    public HttpClient CreateClientForApplicationRole(WebApplicationFactory<Program> host, string applicationId, string role)
    {
        HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateAdminJwt(new Claim(
                DelegatedAdminClaimTypes.AppRole,
                DelegatedAdminAuthorizationService.CreateApplicationRoleValue(applicationId, role))));
        return client;
    }

    public HttpClient CreateClientForApplicationRole(string applicationId, string role)
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateAdminJwt(new Claim(
                DelegatedAdminClaimTypes.AppRole,
                DelegatedAdminAuthorizationService.CreateApplicationRoleValue(applicationId, role))));
        return client;
    }

    public HttpClient CreateClientForTenantRole(string tenantId, string role)
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateAdminJwt(new Claim(
                DelegatedAdminClaimTypes.TenantRole,
                DelegatedAdminAuthorizationService.CreateTenantRoleValue(tenantId, role))));
        return client;
    }

    public HttpClient CreateClientForPlatformRole(string platformRole)
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateAdminJwt(new Claim(DelegatedAdminClaimTypes.PlatformRole, platformRole)));
        return client;
    }

    public HttpClient CreateAuthenticatedClientWithoutRoles()
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateAdminJwt());
        return client;
    }

    public async Task SeedLocalDataAsync()
    {
        using IServiceScope scope = Services.CreateScope();
        LocalDevelopmentSeeder seeder = scope.ServiceProvider.GetRequiredService<LocalDevelopmentSeeder>();
        await seeder.SeedAsync();
    }

    /// <summary>Mints an RS256 runtime token accepted by the seeded OIDC providers.</summary>
    public string CreateRuntimeJwt(string authorizedParty, string? issuer = null, string? audience = null)
    {
        return CreateRuntimeJwt(authorizedParty, RuntimeSigningKey, issuer, audience);
    }

    /// <summary>Mints an RS256 user-flow token carrying a preferred_username claim.</summary>
    public string CreateUserRuntimeJwt(string authorizedParty, string preferredUsername, string? issuer = null, string? audience = null)
    {
        return CreateRuntimeJwt(authorizedParty, RuntimeSigningKey, issuer, audience, preferredUsername);
    }

    /// <summary>Mints a runtime token signed with an unknown key; used to assert 401 on bad signatures.</summary>
    public string CreateUntrustedRuntimeJwt(string authorizedParty)
    {
        using RSA rogueRsa = RSA.Create(2048);
        var rogueKey = new RsaSecurityKey(rogueRsa) { KeyId = "rogue-runtime-key" };
        return CreateRuntimeJwt(authorizedParty, rogueKey, issuer: null, audience: null);
    }

    private static string CreateRuntimeJwt(string authorizedParty, SecurityKey signingKey, string? issuer, string? audience, string? preferredUsername = null)
    {
        List<Claim> claims = [new Claim("azp", authorizedParty)];
        if (preferredUsername is not null)
        {
            claims.Add(new Claim("preferred_username", preferredUsername));
        }

        var token = new JwtSecurityToken(
            issuer ?? RuntimeIssuer,
            audience ?? RuntimeAudience,
            claims,
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(30),
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string CreateAdminJwt(params Claim[] extraClaims)
    {
        List<Claim> claims =
        [
            new Claim(ClaimTypes.Name, "admin@local.test"),
            new Claim(ClaimTypes.Email, "admin@local.test"),
            .. extraClaims,
        ];
        var token = new JwtSecurityToken(
            TestIssuer,
            TestAudience,
            claims,
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(30),
            new SigningCredentials(TestSigningKey, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class TestRuntimeSigningKeyResolver : IRuntimeSigningKeyResolver
    {
        private readonly IReadOnlyCollection<SecurityKey> keys;

        public TestRuntimeSigningKeyResolver(SecurityKey key)
        {
            keys = [key];
        }

        public Task<IReadOnlyCollection<SecurityKey>> ResolveAsync(string jwksUri, CancellationToken cancellationToken)
        {
            return Task.FromResult(keys);
        }
    }
}
