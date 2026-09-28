using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Authorization.Api.Observability;

/// <summary>
/// Readiness check that verifies the application can reach its PostgreSQL database.
/// Mapped to <c>/health/ready</c> (via the <c>ready</c> tag) so an orchestrator only
/// routes traffic once the datastore is reachable; liveness stays independent of it.
/// </summary>
public sealed class DatabaseReadinessHealthCheck : IHealthCheck
{
    /// <summary>Tag used to include this check in the readiness probe.</summary>
    public const string ReadyTag = "ready";

    private readonly AuthorizationDbContext dbContext;

    public DatabaseReadinessHealthCheck(AuthorizationDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        bool canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);
        return canConnect
            ? HealthCheckResult.Healthy("Database connection is available.")
            : HealthCheckResult.Unhealthy("Database connection is unavailable.");
    }
}
