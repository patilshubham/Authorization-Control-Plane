using Authorization.Api.Ai;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Tests.Ai;

/// <summary>
/// Tests for the generic, registry-driven part of F8 (Option B): entities beyond the five
/// hand-written ones are answerable through a closed, reflection-derived, scoped descriptor. These
/// assert the merged planner schema, successful scoped queries, filtering, and the validation gate
/// (unknown field → rejected) for a representative registry entity.
/// </summary>
public sealed class AccessSearchRegistryTests
{
    private const string ApplicationId = "pricing-management";
    private readonly Guid appId = Guid.NewGuid();

    [Fact]
    public void PlannerSchema_IncludesGenericRegistryEntities()
    {
        var names = AccessSearchExecutor.BuildPlannerSchema().Select(s => s.Entity).ToList();

        // The five hand-written entities remain, plus the generic registry entities.
        Assert.Contains("role", names);
        Assert.Contains("sodRule", names);
        Assert.Contains("referenceData", names);
        Assert.Contains("application", names);
        Assert.Contains("oidcProvider", names);
        Assert.Contains("auditEvent", names);
        Assert.Contains("decision", names);
    }

    [Fact]
    public async Task SodRuleEntity_ReturnsScopedRules()
    {
        await using AuthorizationDbContext db = await SeedAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("sodRule", []));

        Assert.True(outcome.IsValid);
        Assert.Equal(2, outcome.Rows.Count);
        Assert.All(outcome.Rows, r => Assert.Equal("SOD_RULE", r.EntityType));
        Assert.Contains(outcome.Rows, r => r.Title == "Submit vs Publish");
    }

    [Fact]
    public async Task SodRuleEntity_FilterBySeverity_NarrowsResults()
    {
        await using AuthorizationDbContext db = await SeedAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("sodRule",
            [new AccessSearchFilter("severity", "eq", "HIGH")]));

        Assert.True(outcome.IsValid);
        Assert.Single(outcome.Rows);
        Assert.Equal("Submit vs Publish", outcome.Rows[0].Title);
    }

    [Fact]
    public async Task RegistryEntity_UnknownField_IsRejected()
    {
        await using AuthorizationDbContext db = await SeedAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("sodRule",
            [new AccessSearchFilter("secretColumn", "eq", "x")]));

        Assert.False(outcome.IsValid);
        Assert.Contains("secretColumn", outcome.Error);
    }

    [Fact]
    public async Task ApplicationEntity_ReturnsAppInScope()
    {
        await using AuthorizationDbContext db = await SeedAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("application", []));

        Assert.True(outcome.IsValid);
        Assert.Single(outcome.Rows);
        Assert.Equal("APPLICATION", outcome.Rows[0].EntityType);
        Assert.Equal("Pricing Management", outcome.Rows[0].Title);
    }

    [Fact]
    public async Task ReferenceDataEntity_FilterByKey_UsesContains()
    {
        await using AuthorizationDbContext db = await SeedAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("referenceData",
            [new AccessSearchFilter("key", "contains", "region")]));

        Assert.True(outcome.IsValid);
        Assert.Single(outcome.Rows);
        Assert.Equal("allowed-regions", outcome.Rows[0].Title);
    }

    [Fact]
    public async Task CountAggregate_RegistryEntity_ReturnsScalarCountRow()
    {
        await using AuthorizationDbContext db = await SeedAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("sodRule", [], "count"));

        Assert.True(outcome.IsValid);
        Assert.Single(outcome.Rows);
        Assert.Equal("COUNT", outcome.Rows[0].EntityType);
        Assert.Equal("2", outcome.Rows[0].Title);
    }

    [Fact]
    public async Task CountAggregate_RegistryEntity_RespectsFilters()
    {
        await using AuthorizationDbContext db = await SeedAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("sodRule",
            [new AccessSearchFilter("severity", "eq", "HIGH")], "count"));

        Assert.True(outcome.IsValid);
        Assert.Equal("1", outcome.Rows[0].Title);
    }

    [Fact]
    public async Task TenantEntity_ReturnsOwningTenantInScope()
    {
        await using AuthorizationDbContext db = await SeedAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("tenant", []));

        Assert.True(outcome.IsValid);
        Assert.Single(outcome.Rows);
        Assert.Equal("TENANT", outcome.Rows[0].EntityType);
        Assert.Equal("Acme Corp", outcome.Rows[0].Title);
    }

    [Fact]
    public void PlannerSchema_IncludesTenantAndRolePermission()
    {
        var names = AccessSearchExecutor.BuildPlannerSchema().Select(s => s.Entity).ToList();
        Assert.Contains("tenant", names);
        Assert.Contains("rolePermission", names);
    }

    [Fact]
    public async Task RelationshipCount_RolePermissions_ReturnsPerRoleCounts()
    {
        await using AuthorizationDbContext db = await SeedAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("role", [], null, "permissions"));

        Assert.True(outcome.IsValid);
        Assert.Equal(2, outcome.Rows.Count);
        // Ordered by count descending: pricing-admin (2) before pricing-viewer (1).
        Assert.Equal("pricing-admin", outcome.Rows[0].Title);
        Assert.Contains("2 permission", outcome.Rows[0].Detail);
        Assert.Equal("pricing-viewer", outcome.Rows[1].Title);
    }

    [Fact]
    public async Task RelationshipCount_PermissionRoles_ReturnsPerPermissionCounts()
    {
        await using AuthorizationDbContext db = await SeedAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("permission", [], null, "roles"));

        Assert.True(outcome.IsValid);
        // price.view is granted by 2 roles, price.publish by 1.
        Assert.Equal("price.view", outcome.Rows[0].Title);
        Assert.Contains("2 role", outcome.Rows[0].Detail);
    }

    [Fact]
    public async Task RelationshipCount_UnknownRelationship_IsRejected()
    {
        await using AuthorizationDbContext db = await SeedAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("role", [], null, "widgets"));

        Assert.False(outcome.IsValid);
        Assert.Contains("widgets", outcome.Error);
    }

    [Fact]
    public async Task Include_RolePermissions_AttachesChildrenAsTree()
    {
        await using AuthorizationDbContext db = await SeedAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec(
            "role", [new AccessSearchFilter("roleKey", "eq", "pricing-admin")], null, null, "permissions"));

        Assert.True(outcome.IsValid);
        Assert.Equal(AccessSearchModes.Tree, outcome.Mode);
        Assert.Single(outcome.Rows);
        Assert.Equal("Pricing Admin", outcome.Rows[0].Title);
        Assert.Equal("pricing-admin", outcome.Rows[0].DeepLinkKey);
        Assert.NotNull(outcome.Rows[0].Children);
        Assert.Equal(2, outcome.Rows[0].Children!.Count);
        Assert.All(outcome.Rows[0].Children!, c => Assert.Equal("PERMISSION", c.EntityType));
        // Children inherit the parent's application id so their deep links resolve.
        Assert.All(outcome.Rows[0].Children!, c => Assert.Equal(ApplicationId, c.ApplicationId));
    }

    [Fact]
    public async Task Include_ApplicationRoles_AttachesChildren()
    {
        await using AuthorizationDbContext db = await SeedAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("application", [], null, null, "roles"));

        Assert.True(outcome.IsValid);
        Assert.Equal(AccessSearchModes.Tree, outcome.Mode);
        Assert.Single(outcome.Rows);
        Assert.Equal(2, outcome.Rows[0].Children!.Count);
    }

    [Fact]
    public async Task Include_UnknownRelationship_IsRejected()
    {
        await using AuthorizationDbContext db = await SeedAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("role", [], null, null, "widgets"));

        Assert.False(outcome.IsValid);
        Assert.Contains("widgets", outcome.Error);
    }

    private Task<AccessSearchOutcome> Run(AuthorizationDbContext db, AccessSearchSpec spec) =>
        new AccessSearchExecutor(db).ExecuteAsync(appId, ApplicationId, spec, CancellationToken.None);

    private async Task<AuthorizationDbContext> SeedAsync()
    {
        var options = new DbContextOptionsBuilder<AuthorizationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AuthorizationDbContext(options);

        var tenantRefId = Guid.NewGuid();
        db.Tenants.Add(new TenantEntity
        {
            Id = tenantRefId,
            TenantId = "acme",
            Name = "Acme Corp",
            Status = "ACTIVE",
        });

        db.Applications.Add(new ApplicationEntity
        {
            Id = appId,
            ApplicationId = ApplicationId,
            Name = "Pricing Management",
            TenantRefId = tenantRefId,
            RiskLevel = "HIGH",
            Status = "ACTIVE",
        });

        db.SodRules.AddRange(
            new SodRuleEntity
            {
                Id = Guid.NewGuid(),
                ApplicationRefId = appId,
                RuleKey = "submit-vs-publish",
                Name = "Submit vs Publish",
                Severity = "HIGH",
                Status = "ACTIVE",
            },
            new SodRuleEntity
            {
                Id = Guid.NewGuid(),
                ApplicationRefId = appId,
                RuleKey = "view-vs-edit",
                Name = "View vs Edit",
                Severity = "MEDIUM",
                Status = "ACTIVE",
            });

        db.ReferenceData.Add(new ReferenceDataEntity
        {
            Id = Guid.NewGuid(),
            ApplicationRefId = appId,
            Key = "allowed-regions",
            Value = "[\"US\",\"EU\"]",
            Status = "ACTIVE",
        });

        // Roles + permissions + PUBLISHED grants for the relationship-count tests:
        // pricing-admin grants 2 permissions, pricing-viewer grants 1.
        var roleAdmin = Guid.NewGuid();
        var roleViewer = Guid.NewGuid();
        db.Roles.AddRange(
            new RoleEntity { Id = roleAdmin, ApplicationRefId = appId, RoleKey = "pricing-admin", Name = "Pricing Admin", Privileged = true, RiskLevel = "HIGH", Status = "ACTIVE" },
            new RoleEntity { Id = roleViewer, ApplicationRefId = appId, RoleKey = "pricing-viewer", Name = "Pricing Viewer", Privileged = false, RiskLevel = "LOW", Status = "ACTIVE" });

        var permView = Guid.NewGuid();
        var permPublish = Guid.NewGuid();
        db.Permissions.AddRange(
            new PermissionEntity { Id = permView, ApplicationRefId = appId, PermissionKey = "price.view", Resource = "price", Action = "view", RiskLevel = "LOW", Status = "ACTIVE" },
            new PermissionEntity { Id = permPublish, ApplicationRefId = appId, PermissionKey = "price.publish", Resource = "price", Action = "publish", RiskLevel = "HIGH", Status = "ACTIVE" });

        db.RolePermissions.AddRange(
            new RolePermissionEntity { Id = Guid.NewGuid(), ApplicationRefId = appId, RoleRefId = roleAdmin, PermissionRefId = permView, State = "PUBLISHED" },
            new RolePermissionEntity { Id = Guid.NewGuid(), ApplicationRefId = appId, RoleRefId = roleAdmin, PermissionRefId = permPublish, State = "PUBLISHED" },
            new RolePermissionEntity { Id = Guid.NewGuid(), ApplicationRefId = appId, RoleRefId = roleViewer, PermissionRefId = permView, State = "PUBLISHED" });

        await db.SaveChangesAsync();
        return db;
    }
}
