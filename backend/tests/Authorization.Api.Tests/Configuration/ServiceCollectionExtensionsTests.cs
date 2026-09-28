using Authorization.Api.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Authorization.Api.Tests.Configuration;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAuthorizationControlPlaneConfiguration_BindsRequiredOptions()
    {
        using ServiceProvider provider = CreateProvider(CreateValidSettings());

        var database = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        var oidc = provider.GetRequiredService<IOptions<OidcOptions>>().Value;

        Assert.Equal("Host=postgres;Database=authorization", database.Postgres);
        Assert.Equal("authorization-api", oidc.Audience);
    }

    [Fact]
    public void AddAuthorizationControlPlaneConfiguration_RejectsMissingPostgresConnectionString()
    {
        Dictionary<string, string?> settings = CreateValidSettings();
        settings.Remove("ConnectionStrings:Postgres");

        using ServiceProvider provider = CreateProvider(settings);

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<DatabaseOptions>>().Value);
    }

    private static ServiceProvider CreateProvider(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddAuthorizationControlPlaneConfiguration(configuration);

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static Dictionary<string, string?> CreateValidSettings()
    {
        return new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=postgres;Database=authorization",
            ["Oidc:Authority"] = "http://keycloak:8080/realms/authorization-local",
            ["Oidc:Audience"] = "authorization-api",
            ["Oidc:PortalClientId"] = "authorization-portal",
        };
    }
}