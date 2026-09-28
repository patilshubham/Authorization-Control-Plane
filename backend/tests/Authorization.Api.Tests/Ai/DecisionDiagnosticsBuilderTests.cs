using Authorization.Ai;
using Authorization.Api.Ai;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Tests.Ai;

/// <summary>
/// Unit tests for <see cref="DecisionDiagnosticsBuilder"/> against an in-memory store. These verify
/// the builder surfaces the correct root-cause facts for each denial scenario.
/// </summary>
public sealed class DecisionDiagnosticsBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 13, 0, 0, 0, TimeSpan.Zero);

    private readonly Guid appId = Guid.NewGuid();
    private readonly Guid pricingLeadRoleId = Guid.NewGuid();
    private readonly Guid pricingViewerRoleId = Guid.NewGuid();
    private readonly Guid publishPermissionId = Guid.NewGuid();
    private readonly Guid viewPermissionId = Guid.NewGuid();

    [Fact]
    public async Task Build_UnknownResourceType_ReportsMissingPermissionAndKnownTypes()
    {
        await using AuthorizationDbContext db = await SeedAsync();
        var builder = new DecisionDiagnosticsBuilder(db, new FixedTimeProvider(Now));

        DecisionDiagnostics result = await builder.BuildAsync(
            appId, "USER", "user7.lead@icis.com", "prices", "publish", [], [], CancellationToken.None);

        Assert.False(result.PermissionExists);
        Assert.False(result.ResourceTypeKnown);
        Assert.Contains("price", result.KnownResourceTypes);
    }

    [Fact]
    public async Task Build_KnownResourceUnknownAction_ListsAvailableActions()
    {
        await using AuthorizationDbContext db = await SeedAsync();
        var builder = new DecisionDiagnosticsBuilder(db, new FixedTimeProvider(Now));

        DecisionDiagnostics result = await builder.BuildAsync(
            appId, "USER", "user7.lead@icis.com", "price", "delete", [], [], CancellationToken.None);

        Assert.False(result.PermissionExists);
        Assert.True(result.ResourceTypeKnown);
        Assert.Contains("publish", result.AvailableActionsForResourceType);
        Assert.Contains("view", result.AvailableActionsForResourceType);
    }

    [Fact]
    public async Task Build_UnknownSubjectWithTypo_SuggestsSimilarEmail()
    {
        await using AuthorizationDbContext db = await SeedAsync();
        var builder = new DecisionDiagnosticsBuilder(db, new FixedTimeProvider(Now));

        DecisionDiagnostics result = await builder.BuildAsync(
            appId, "USER", "user7.led@icis.com", "price", "publish", [], [], CancellationToken.None);

        Assert.False(result.SubjectKnown);
        Assert.Contains("user7.lead@icis.com", result.SimilarKnownSubjectEmails);
    }

    [Fact]
    public async Task Build_SubjectHoldsNonGrantingRole_ListsGrantingRoles()
    {
        await using AuthorizationDbContext db = await SeedAsync();
        var builder = new DecisionDiagnosticsBuilder(db, new FixedTimeProvider(Now));

        DecisionDiagnostics result = await builder.BuildAsync(
            appId, "USER", "viewer@icis.com", "price", "publish", [], [], CancellationToken.None);

        Assert.True(result.SubjectKnown);
        Assert.Equal(1, result.ActiveAssignmentCount);
        Assert.Contains("pricing-viewer", result.SubjectRoleKeys);
        Assert.Contains("pricing-lead", result.RolesGrantingPermission);
        Assert.DoesNotContain("pricing-lead", result.SubjectRoleKeys);
    }

    [Fact]
    public async Task Build_RevokedAssignment_CountsRevoked()
    {
        await using AuthorizationDbContext db = await SeedAsync();
        db.Assignments.Add(new AssignmentEntity
        {
            Id = Guid.NewGuid(),
            ApplicationRefId = appId,
            SubjectType = "USER",
            SubjectEmail = "revoked@icis.com",
            RoleRefId = pricingLeadRoleId,
            ValidFrom = Now.AddDays(-10),
            RevokedAt = Now.AddDays(-1),
            State = "REVOKED",
        });
        await db.SaveChangesAsync();
        var builder = new DecisionDiagnosticsBuilder(db, new FixedTimeProvider(Now));

        DecisionDiagnostics result = await builder.BuildAsync(
            appId, "USER", "revoked@icis.com", "price", "publish", [], [], CancellationToken.None);

        Assert.True(result.SubjectKnown);
        Assert.Equal(0, result.ActiveAssignmentCount);
        Assert.Equal(1, result.RevokedAssignmentCount);
    }

    [Fact]
    public async Task Build_Policies_ReportsMatchedFlagAndReferencedContext()
    {
        await using AuthorizationDbContext db = await SeedAsync();
        db.Policies.Add(new PolicyEntity
        {
            Id = Guid.NewGuid(),
            ApplicationRefId = appId,
            PolicyKey = "deny-self-publish",
            PermissionRefId = publishPermissionId,
            Effect = "DENY",
            Conditions = "{\"match\":\"all\",\"conditions\":[{\"attribute\":\"context.isAuthor\",\"operator\":\"eq\",\"value\":\"true\"}]}",
            State = "PUBLISHED",
        });
        await db.SaveChangesAsync();
        var builder = new DecisionDiagnosticsBuilder(db, new FixedTimeProvider(Now));

        DecisionDiagnostics result = await builder.BuildAsync(
            appId, "USER", "viewer@icis.com", "price", "publish", ["deny-self-publish"], ["market"], CancellationToken.None);

        PolicyDiagnostic policy = Assert.Single(result.RelevantPolicies);
        Assert.Equal("deny-self-publish", policy.PolicyKey);
        Assert.True(policy.Matched);
        Assert.Contains("isAuthor", result.ReferencedContextAttributes);
        Assert.Contains("market", result.ProvidedContextKeys);
    }

    private async Task<AuthorizationDbContext> SeedAsync()
    {
        var options = new DbContextOptionsBuilder<AuthorizationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AuthorizationDbContext(options);

        db.Applications.Add(new ApplicationEntity
        {
            Id = appId,
            ApplicationId = "pricing-management",
            Name = "Pricing Management",
            TenantRefId = Guid.NewGuid(),
            Status = "ACTIVE",
        });

        db.Roles.AddRange(
            new RoleEntity { Id = pricingLeadRoleId, ApplicationRefId = appId, RoleKey = "pricing-lead", Name = "Pricing Lead" },
            new RoleEntity { Id = pricingViewerRoleId, ApplicationRefId = appId, RoleKey = "pricing-viewer", Name = "Pricing Viewer" });

        db.Permissions.AddRange(
            new PermissionEntity { Id = publishPermissionId, ApplicationRefId = appId, PermissionKey = "price.publish", Resource = "price", Action = "publish", Status = "ACTIVE" },
            new PermissionEntity { Id = viewPermissionId, ApplicationRefId = appId, PermissionKey = "price.view", Resource = "price", Action = "view", Status = "ACTIVE" });

        // Only pricing-lead grants publish.
        db.RolePermissions.Add(new RolePermissionEntity
        {
            Id = Guid.NewGuid(),
            ApplicationRefId = appId,
            RoleRefId = pricingLeadRoleId,
            PermissionRefId = publishPermissionId,
            State = "PUBLISHED",
        });

        // A known lead subject (for typo matching) and a viewer subject with a non-granting role.
        db.Assignments.AddRange(
            new AssignmentEntity
            {
                Id = Guid.NewGuid(),
                ApplicationRefId = appId,
                SubjectType = "USER",
                SubjectEmail = "user7.lead@icis.com",
                RoleRefId = pricingLeadRoleId,
                ValidFrom = Now.AddDays(-10),
                State = "ACTIVE",
            },
            new AssignmentEntity
            {
                Id = Guid.NewGuid(),
                ApplicationRefId = appId,
                SubjectType = "USER",
                SubjectEmail = "viewer@icis.com",
                RoleRefId = pricingViewerRoleId,
                ValidFrom = Now.AddDays(-10),
                State = "ACTIVE",
            });

        await db.SaveChangesAsync();
        return db;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset now = now;

        public override DateTimeOffset GetUtcNow() => now;
    }
}
