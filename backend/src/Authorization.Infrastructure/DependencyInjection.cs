using Authorization.Infrastructure.Persistence;
using Authorization.Infrastructure.RuntimeAuthorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Authorization.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAuthorizationInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

        services.AddDbContext<AuthorizationDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsAssembly(typeof(AuthorizationDbContext).Assembly.FullName));
        });

        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddScoped<IDbConnectionFactory, NpgsqlConnectionFactory>();
        services.AddScoped<LocalDevelopmentSeeder>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IAuthorizationPolicyEngine, EfAuthorizationPolicyEngine>();
        services.AddSingleton<InProcessDecisionOutbox>();
        services.AddSingleton<IDecisionRecorder>(provider => provider.GetRequiredService<InProcessDecisionOutbox>());
        services.AddHostedService<DecisionPersistenceWorker>();

        return services;
    }
}