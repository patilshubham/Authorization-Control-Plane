using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Authorization.Infrastructure.RuntimeAuthorization;

namespace Authorization.Api.Observability;

public static class ServiceCollectionExtensions
{
    public const string ServiceName = "Authorization.Api";

    public static IServiceCollection AddAuthorizationObservability(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<DatabaseReadinessHealthCheck>(
                "database",
                tags: [DatabaseReadinessHealthCheck.ReadyTag]);
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing => tracing
                .AddSource(EfAuthorizationPolicyEngine.ActivitySourceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation());

        return services;
    }
}
