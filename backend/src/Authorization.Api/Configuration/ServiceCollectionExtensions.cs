using Microsoft.Extensions.Options;

namespace Authorization.Api.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAuthorizationControlPlaneConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddValidatedOptions<DatabaseOptions>(configuration, DatabaseOptions.SectionName);
        services.AddValidatedOptions<OidcOptions>(configuration, OidcOptions.SectionName);
        services.AddValidatedOptions<PortalOptions>(configuration, PortalOptions.SectionName);

        return services;
    }

    private static OptionsBuilder<TOptions> AddValidatedOptions<TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where TOptions : class
    {
        return services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }
}
