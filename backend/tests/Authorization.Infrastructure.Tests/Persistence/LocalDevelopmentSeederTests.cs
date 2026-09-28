using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Authorization.Infrastructure.Tests.Persistence;

public sealed class LocalDevelopmentSeederTests
{
    [Fact]
    public async Task SeedAsync_CreatesRequiredLocalDataAndIsIdempotent()
    {
        await using AuthorizationDbContext dbContext = CreateDbContext();
        var seeder = new LocalDevelopmentSeeder(dbContext, new ConfigurationBuilder().Build());

        await seeder.SeedAsync();
        SeedCounts firstRun = await CountSeedDataAsync(dbContext);
        await seeder.SeedAsync();
        SeedCounts secondRun = await CountSeedDataAsync(dbContext);

        Assert.Equal(new SeedCounts(4, 5, 18, 31, 82, 32, 8, 10, 1), firstRun);
        Assert.Equal(firstRun, secondRun);
        Assert.True(await dbContext.Policies.AnyAsync(policy => policy.Effect == "DENY"));
        Assert.True(await dbContext.Policies.AnyAsync(policy => policy.State == "DRAFT"));
        Assert.True(await dbContext.SodRules.AnyAsync(rule => rule.Status == "ACTIVE"));
        Assert.True(await dbContext.ReviewCampaigns.AnyAsync(campaign => campaign.Status == "ACTIVE"));
        Assert.True(await dbContext.ReviewItems.AnyAsync());
        Assert.True(await dbContext.ReferenceData.AnyAsync());
        Assert.Equal(2, await dbContext.Tenants.CountAsync());
        Assert.True(await dbContext.Applications.AllAsync(app => app.TenantRefId != Guid.Empty));

        // Pricing Management demonstrates the full feature set across 20 distinct users.
        Guid pricingAppId = await dbContext.Applications
            .Where(app => app.ApplicationId == "pricing-management")
            .Select(app => app.Id)
            .SingleAsync();
        int pricingUsers = await dbContext.Assignments
            .Where(assignment => assignment.ApplicationRefId == pricingAppId && assignment.SubjectType == "USER")
            .Select(assignment => assignment.SubjectEmail)
            .Distinct()
            .CountAsync();
        Assert.Equal(20, pricingUsers);

        // Privileged roles must be time-boxed: no ACTIVE assignment of a privileged role may stand
        // without an expiry (a REVOKED historical row is fine, since it's already inactive).
        List<Guid> privilegedRoleIds = await dbContext.Roles
            .Where(role => role.Privileged)
            .Select(role => role.Id)
            .ToListAsync();
        Assert.False(await dbContext.Assignments.AnyAsync(assignment =>
            assignment.State == "ACTIVE" && privilegedRoleIds.Contains(assignment.RoleRefId) && assignment.ValidUntil == null));
    }

    private static AuthorizationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AuthorizationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AuthorizationDbContext(options);
    }

    private static async Task<SeedCounts> CountSeedDataAsync(AuthorizationDbContext dbContext)
    {
        return new SeedCounts(
            await dbContext.Applications.CountAsync(),
            await dbContext.OidcProviders.CountAsync(),
            await dbContext.Roles.CountAsync(),
            await dbContext.Permissions.CountAsync(),
            await dbContext.RolePermissions.CountAsync(),
            await dbContext.Assignments.CountAsync(),
            await dbContext.AssignmentAttributes.CountAsync(),
            await dbContext.Policies.CountAsync(),
            await dbContext.SodRules.CountAsync());
    }

    private sealed record SeedCounts(
        int Applications,
        int OidcProviders,
        int Roles,
        int Permissions,
        int RolePermissions,
        int Assignments,
        int AssignmentAttributes,
        int Policies,
        int SodRules);
}
