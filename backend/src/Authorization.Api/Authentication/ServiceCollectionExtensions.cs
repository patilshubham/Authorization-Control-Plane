using Authorization.Api.Authorization;
using Authorization.Api.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Authorization.Api.Authentication;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAuthorizationControlPlaneAuthentication(this IServiceCollection services)
    {
        services.AddSingleton<JwtTokenValidator>();
        services.AddSingleton<IRuntimeSigningKeyResolver, JwksRuntimeSigningKeyResolver>();
        services.AddHttpClient(JwksRuntimeSigningKeyResolver.HttpClientName);
        services.AddScoped<RuntimeCallerAuthenticator>();
        services.AddSingleton<DelegatedAdminAuthorizationService>();
        services.AddSingleton<IAuthorizationHandler, DelegatedAdminAuthorizationHandler>();

        services.AddAuthentication()
            .AddJwtBearer(ApiAuthenticationSchemes.AdminJwt);

        services.AddOptions<JwtBearerOptions>(ApiAuthenticationSchemes.AdminJwt)
            .Configure<IOptions<OidcOptions>>((options, oidcAccessor) =>
            {
                OidcOptions oidcOptions = oidcAccessor.Value;

                options.Authority = oidcOptions.Authority;
                options.Audience = oidcOptions.Audience;
                options.RequireHttpsMetadata = false;

                // When the public issuer URL (Authority) is not reachable from within the
                // API's network (common in Docker), use a back-channel metadata address for
                // discovery/JWKS while still validating the token against the public issuer.
                if (!string.IsNullOrWhiteSpace(oidcOptions.MetadataAddress))
                {
                    options.MetadataAddress = oidcOptions.MetadataAddress;
                }

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateAudience = true,
                    ValidAudience = oidcOptions.Audience,
                    ValidateIssuer = true,
                    ValidIssuer = oidcOptions.Authority,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    RequireSignedTokens = true,
                    RoleClaimType = DelegatedAdminClaimTypes.PlatformRole,
                    NameClaimType = "preferred_username",
                };
            });

        var authorizationBuilder = services.AddAuthorizationBuilder()
            .AddPolicy(DelegatedAdminPolicyNames.AdminApi, policy => policy
                .AddAuthenticationSchemes(ApiAuthenticationSchemes.AdminJwt)
                .RequireAuthenticatedUser())
            .AddPolicy(DelegatedAdminPolicyNames.PlatformAdmin, policy => policy
                .AddAuthenticationSchemes(ApiAuthenticationSchemes.AdminJwt)
                .RequireAuthenticatedUser()
                .RequireRole(DelegatedAdminRoles.PlatformSuperAdmin))
            .AddPolicy("RuntimeApi", policy => policy.RequireAuthenticatedUser());

        foreach (DelegatedAdminCapability capability in Enum.GetValues<DelegatedAdminCapability>())
        {
            authorizationBuilder.AddPolicy(DelegatedAdminPolicyNames.For(capability), policy => policy
                .AddAuthenticationSchemes(ApiAuthenticationSchemes.AdminJwt)
                .RequireAuthenticatedUser()
                .AddRequirements(new DelegatedAdminRequirement(capability)));
        }

        return services;
    }
}
