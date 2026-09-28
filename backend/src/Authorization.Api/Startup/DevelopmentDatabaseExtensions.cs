using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Authorization.Api.Startup;

public static class DevelopmentDatabaseExtensions
{
    public static async Task<WebApplication> UseDevelopmentDatabaseSetupAsync(this WebApplication app)
    {
        ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DevelopmentDatabaseSetup");

        if (!app.Environment.IsDevelopment()
            || !app.Configuration.GetValue("Database:RunDevelopmentSetup", defaultValue: true))
        {
            logger.LogInformation("Development database setup skipped (environment is not Development or Database:RunDevelopmentSetup is false).");
            return app;
        }

        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        AuthorizationDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        await dbContext.Database.MigrateAsync();
        logger.LogInformation("Development database migrations applied.");

        if (!app.Configuration.GetValue("Database:SeedDevelopmentData", defaultValue: true))
        {
            logger.LogInformation("Development seed data skipped (Database:SeedDevelopmentData is false).");
            return app;
        }

        LocalDevelopmentSeeder seeder = scope.ServiceProvider.GetRequiredService<LocalDevelopmentSeeder>();
        await seeder.SeedAsync();
        logger.LogInformation("Development seed data applied.");

        await VerifySeededIssuersAsync(app, dbContext, logger);

        return app;
    }

    /// <summary>
    /// Compares the issuer configured on seeded OIDC providers against the identity provider's actual
    /// discovery issuer. A mismatch means runtime caller tokens will fail validation
    /// (<c>CALLER_UNAUTHENTICATED</c>), so it is surfaced loudly at startup rather than only at request
    /// time. Best-effort and never fatal: discovery failures are logged and ignored.
    /// </summary>
    private static async Task VerifySeededIssuersAsync(WebApplication app, AuthorizationDbContext dbContext, ILogger logger)
    {
        string? metadataAddress = app.Configuration["Oidc:MetadataAddress"];
        if (string.IsNullOrWhiteSpace(metadataAddress))
        {
            return;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using JsonDocument discovery = JsonDocument.Parse(await http.GetStringAsync(metadataAddress));
            if (!discovery.RootElement.TryGetProperty("issuer", out JsonElement issuerElement)
                || issuerElement.GetString() is not { Length: > 0 } discoveryIssuer)
            {
                return;
            }

            List<string> seededIssuers = await dbContext.OidcProviders
                .AsNoTracking()
                .Select(provider => provider.Issuer)
                .Distinct()
                .ToListAsync();

            foreach (string issuer in seededIssuers.Where(issuer => !string.Equals(issuer, discoveryIssuer, StringComparison.Ordinal)))
            {
                logger.LogWarning(
                    "Seeded OIDC provider issuer '{SeededIssuer}' does not match the identity provider discovery issuer '{DiscoveryIssuer}'. Runtime caller tokens will fail validation with CALLER_UNAUTHENTICATED.",
                    issuer,
                    discoveryIssuer);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(exception, "Unable to verify seeded OIDC provider issuers against discovery at {MetadataAddress}.", metadataAddress);
        }
    }
}
