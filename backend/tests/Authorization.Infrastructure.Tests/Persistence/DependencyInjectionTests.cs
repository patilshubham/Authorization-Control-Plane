using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Authorization.Infrastructure.Tests.Persistence;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddAuthorizationInfrastructure_RegistersPostgresDbContextAndDapperConnectionFactory()
    {
        using ServiceProvider provider = CreateServices(CreateConfiguration("Host=localhost;Database=authorization"))
            .BuildServiceProvider(validateScopes: true);

        using IServiceScope scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();

        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", dbContext.Database.ProviderName);
        Assert.Contains("Database=authorization", connectionFactory.ConnectionString);
    }

    [Fact]
    public void AddAuthorizationInfrastructure_RejectsMissingPostgresConnectionString()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        Assert.Throws<InvalidOperationException>(() => services.AddAuthorizationInfrastructure(configuration));
    }

    [Fact]
    public void AuthorizationDbContextFactory_CreatesDesignTimeContextForMigrations()
    {
        var factory = new AuthorizationDbContextFactory();

        using AuthorizationDbContext dbContext = factory.CreateDbContext([]);

        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", dbContext.Database.ProviderName);
    }

    private static IServiceCollection CreateServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddAuthorizationInfrastructure(configuration);
        return services;
    }

    private static IConfiguration CreateConfiguration(string postgresConnectionString)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = postgresConnectionString,
            })
            .Build();
    }
}