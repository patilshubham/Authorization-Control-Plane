using Authorization.Ai;
using Authorization.Api.Ai;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Tests.Ai;

/// <summary>
/// Unit tests for <see cref="AccessSearchExecutor"/>. The executor is the trusted boundary for F8:
/// it validates a closed spec (rejecting unknown entities/fields/operators) and executes only
/// parameterized, read-only EF queries. These tests seed a small pricing-like model and assert both
/// the validation gate and the per-entity query results, including deep-link citations.
/// </summary>
public sealed class AccessSearchExecutorTests
{
    private const string ApplicationId = "pricing-management";
    private readonly Guid appId = Guid.NewGuid();
    private readonly Guid tenantRefId = Guid.NewGuid();

    [Fact]
    public async Task UnknownEntity_IsRejected()
    {
        await using AuthorizationDbContext db = CreateContext();
        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("widget", []));

        Assert.False(outcome.IsValid);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public async Task UnknownField_IsRejected()
    {
        await using AuthorizationDbContext db = CreateContext();
        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("role",
            [new AccessSearchFilter("secretColumn", "eq", "x")]));

        Assert.False(outcome.IsValid);
        Assert.Contains("secretColumn", outcome.Error);
    }

    [Fact]
    public async Task UnknownOperator_IsRejected()
    {
        await using AuthorizationDbContext db = CreateContext();
        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("role",
            [new AccessSearchFilter("roleKey", "regex", "x")]));

        Assert.False(outcome.IsValid);
        Assert.Contains("regex", outcome.Error);
    }

    [Fact]
    public async Task InjectionValue_IsTreatedAsLiteral_AndReturnsNoRows()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();

        // A SQL-looking value must be treated as a plain literal (parameterized), matching nothing.
        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("role",
            [new AccessSearchFilter("roleKey", "eq", "'; DROP TABLE Roles;--")]));

        Assert.True(outcome.IsValid);
        Assert.Empty(outcome.Rows);
    }

    [Fact]
    public async Task RoleQuery_ByGrantAction_ReturnsGrantingRoles()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("role",
            [new AccessSearchFilter("action", "eq", "publish")]));

        Assert.True(outcome.IsValid);
        // pricing-lead and pricing-admin both grant price.publish.
        Assert.Contains(outcome.Rows, r => r.DeepLinkKey == "pricing-lead");
        Assert.Contains(outcome.Rows, r => r.DeepLinkKey == "pricing-admin");
        Assert.DoesNotContain(outcome.Rows, r => r.DeepLinkKey == "pricing-analyst");
        Assert.All(outcome.Rows, r => Assert.Equal("role", r.DeepLinkKind));
    }

    [Fact]
    public async Task SubjectQuery_WhoCanPublish_ReturnsActiveSubjectsOnly()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("subject",
            [new AccessSearchFilter("action", "eq", "publish")]));

        Assert.True(outcome.IsValid);
        // user7.lead has an ACTIVE lead assignment; user8.admin is REVOKED and must be excluded.
        Assert.Contains(outcome.Rows, r => r.DeepLinkKey == "user7.lead@icis.com");
        Assert.DoesNotContain(outcome.Rows, r => r.DeepLinkKey == "user8.admin@icis.com");
        Assert.All(outcome.Rows, r => Assert.Equal("user", r.DeepLinkKind));
    }

    [Fact]
    public async Task PermissionQuery_ByResourceAndAction_ReturnsMatch()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("permission",
            [new AccessSearchFilter("resource", "eq", "price"), new AccessSearchFilter("action", "eq", "publish")]));

        Assert.True(outcome.IsValid);
        AccessSearchRow row = Assert.Single(outcome.Rows);
        Assert.Equal("price.publish", row.DeepLinkKey);
        Assert.Equal("permission", row.DeepLinkKind);
    }

    [Fact]
    public async Task PolicyQuery_ByEffect_ReturnsDenyPolicies()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("policy",
            [new AccessSearchFilter("effect", "eq", "DENY")]));

        Assert.True(outcome.IsValid);
        Assert.NotEmpty(outcome.Rows);
        Assert.All(outcome.Rows, r => Assert.Equal("policy", r.DeepLinkKind));
        Assert.Contains(outcome.Rows, r => r.DeepLinkKey == "deny-self-publish");
    }

    [Fact]
    public async Task AssignmentQuery_BySubject_ReturnsAssignments()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("assignment",
            [new AccessSearchFilter("subject", "contains", "user7")]));

        Assert.True(outcome.IsValid);
        Assert.NotEmpty(outcome.Rows);
        Assert.All(outcome.Rows, r => Assert.Equal("user", r.DeepLinkKind));
    }

    [Fact]
    public async Task RoleQuery_NoFilters_ReturnsAllActiveRoles()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("role", []));

        Assert.True(outcome.IsValid);
        Assert.Equal(4, outcome.Rows.Count);
    }

    [Fact]
    public async Task RoleQuery_StatusNotActive_ReturnsInactiveRolesOnly()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();
        db.Roles.Add(RoleWithStatus("pricing-legacy", "DISABLED"));
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("role",
            [new AccessSearchFilter("status", "neq", "ACTIVE")]));

        Assert.True(outcome.IsValid);
        AccessSearchRow row = Assert.Single(outcome.Rows);
        Assert.Equal("pricing-legacy", row.DeepLinkKey);
    }

    [Fact]
    public async Task RoleCount_InactiveRoles_ExcludesActive()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();
        db.Roles.Add(RoleWithStatus("pricing-legacy", "DEPRECATED"));
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("role",
            [new AccessSearchFilter("status", "neq", "ACTIVE")], "count"));

        Assert.True(outcome.IsValid);
        Assert.Equal(AccessSearchModes.Count, outcome.Mode);
        Assert.Equal("1", Assert.Single(outcome.Rows).Title);
    }

    [Fact]
    public async Task RoleCount_AttachesMatchingRecordsAsSample()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();
        db.Roles.Add(RoleWithStatus("pricing-legacy", "DEPRECATED"));
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("role",
            [new AccessSearchFilter("status", "neq", "ACTIVE")], "count"));

        Assert.True(outcome.IsValid);
        AccessSearchRow countRow = Assert.Single(outcome.Rows);
        Assert.Equal("1", countRow.Title);
        // The number is now auditable: the matching record travels with the count as a sample child.
        AccessSearchRow sample = Assert.Single(countRow.Children!);
        Assert.Equal("pricing-legacy", sample.DeepLinkKey);
    }

    [Fact]
    public async Task CountPlatform_CrossAppEntity_IsNotDoubleCountedAcrossApps()
    {
        // One tenant that owns two applications. A tenant is cross-application, so a platform-wide
        // count that iterates each app must still report the single distinct tenant — not one per
        // app. This guards the regression where the total was summed per application (2) while the
        // de-duplicated sample showed 1, producing a misleading "showing first 1 of 2".
        var options = new DbContextOptionsBuilder<AuthorizationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AuthorizationDbContext(options);

        Guid tenantRefId = Guid.NewGuid();
        db.Tenants.Add(new TenantEntity { Id = tenantRefId, TenantId = "shared-tenant", Name = "Shared", Status = "ACTIVE" });
        Guid appRefA = Guid.NewGuid();
        Guid appRefB = Guid.NewGuid();
        db.Applications.AddRange(
            new ApplicationEntity { Id = appRefA, ApplicationId = "app-a", Name = "App A", TenantRefId = tenantRefId, Status = "ACTIVE" },
            new ApplicationEntity { Id = appRefB, ApplicationId = "app-b", Name = "App B", TenantRefId = tenantRefId, Status = "ACTIVE" });
        await db.SaveChangesAsync();

        int count = await new AccessSearchExecutor(db).CountPlatformAsync(
            [(appRefA, "app-a"), (appRefB, "app-b")],
            new AccessSearchSpec("tenant", [], "count"),
            CancellationToken.None);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GroupPlatform_ApplicationsPerTenant_CountsEveryAppInScope_NotOnePerApp()
    {
        // A tenant that owns three applications, plus a second tenant that owns one. "How many
        // applications per tenant" is a relationship count over a cross-application parent, so it must
        // be evaluated once over the whole accessible scope. The regression this guards: the platform
        // search ran the group inside its per-application loop, so each tenant only saw the single
        // application in that iteration's scope and every tenant reported "1 applications".
        var options = new DbContextOptionsBuilder<AuthorizationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AuthorizationDbContext(options);

        Guid squad1 = Guid.NewGuid();
        Guid squad2 = Guid.NewGuid();
        db.Tenants.AddRange(
            new TenantEntity { Id = squad1, TenantId = "squad-1", Name = "Squad 1", Status = "ACTIVE" },
            new TenantEntity { Id = squad2, TenantId = "squad-2", Name = "Squad 2", Status = "ACTIVE" });

        Guid a1 = Guid.NewGuid();
        Guid a2 = Guid.NewGuid();
        Guid a3 = Guid.NewGuid();
        Guid b1 = Guid.NewGuid();
        db.Applications.AddRange(
            new ApplicationEntity { Id = a1, ApplicationId = "s1-app-1", Name = "S1 App 1", TenantRefId = squad1, Status = "ACTIVE" },
            new ApplicationEntity { Id = a2, ApplicationId = "s1-app-2", Name = "S1 App 2", TenantRefId = squad1, Status = "ACTIVE" },
            new ApplicationEntity { Id = a3, ApplicationId = "s1-app-3", Name = "S1 App 3", TenantRefId = squad1, Status = "ACTIVE" },
            new ApplicationEntity { Id = b1, ApplicationId = "s2-app-1", Name = "S2 App 1", TenantRefId = squad2, Status = "ACTIVE" });
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteGroupPlatformAsync(
            [(a1, "s1-app-1"), (a2, "s1-app-2"), (a3, "s1-app-3"), (b1, "s2-app-1")],
            new AccessSearchSpec("tenant", [], GroupByRelationship: "applications"),
            CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal(AccessSearchModes.Group, outcome.Mode);

        AccessSearchRow squad1Row = Assert.Single(outcome.Rows, r => r.DeepLinkKey == "squad-1");
        Assert.Equal("3 applications", squad1Row.Detail);

        AccessSearchRow squad2Row = Assert.Single(outcome.Rows, r => r.DeepLinkKey == "squad-2");
        Assert.Equal("1 applications", squad2Row.Detail);
    }

    [Fact]
    public async Task GroupPlatform_EntitiesPerTenant_RollUpAcrossApplications()
    {
        // squad-1 owns two applications, squad-2 owns one. Roles/permissions/policies/assignments are
        // application-scoped, so a per-tenant roll-up must aggregate across the tenant's applications
        // (tenant → application → child). Inactive rows (a DISABLED role/permission, a REVOKED
        // assignment) are excluded, matching the resolvers.
        var options = new DbContextOptionsBuilder<AuthorizationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AuthorizationDbContext(options);

        Guid s1 = Guid.NewGuid();
        Guid s2 = Guid.NewGuid();
        db.Tenants.AddRange(
            new TenantEntity { Id = s1, TenantId = "squad-1", Name = "Squad 1", Status = "ACTIVE" },
            new TenantEntity { Id = s2, TenantId = "squad-2", Name = "Squad 2", Status = "ACTIVE" });

        Guid a1 = Guid.NewGuid();
        Guid a2 = Guid.NewGuid();
        Guid b1 = Guid.NewGuid();
        db.Applications.AddRange(
            new ApplicationEntity { Id = a1, ApplicationId = "s1-app-1", Name = "S1 App 1", TenantRefId = s1, Status = "ACTIVE" },
            new ApplicationEntity { Id = a2, ApplicationId = "s1-app-2", Name = "S1 App 2", TenantRefId = s1, Status = "ACTIVE" },
            new ApplicationEntity { Id = b1, ApplicationId = "s2-app-1", Name = "S2 App 1", TenantRefId = s2, Status = "ACTIVE" });

        // squad-1: 3 ACTIVE roles (+1 DISABLED, excluded); squad-2: 1.
        db.Roles.AddRange(
            NewRole(a1, "r1a"), NewRole(a1, "r1b"), NewRole(a2, "r2"),
            NewRole(a1, "r-disabled", status: "DISABLED"), NewRole(b1, "rb"));
        // squad-1: 2 ACTIVE permissions (+1 DISABLED, excluded); squad-2: 1.
        db.Permissions.AddRange(
            NewPermission(a1, "p1"), NewPermission(a2, "p2"),
            NewPermission(a1, "p-disabled", status: "DISABLED"), NewPermission(b1, "pb"));
        // squad-1: 3 policies; squad-2: 1 (no status filter for policies).
        db.Policies.AddRange(
            NewPolicy(a1, "pol1"), NewPolicy(a2, "pol2"), NewPolicy(a2, "pol3"), NewPolicy(b1, "polb"));
        // squad-1: 2 ACTIVE assignments (+1 REVOKED, excluded); squad-2: 1.
        db.Assignments.AddRange(
            NewAssignment(a1, "u1@x.test"), NewAssignment(a2, "u2@x.test"),
            NewAssignment(a1, "u3@x.test", state: "REVOKED"), NewAssignment(b1, "ub@x.test"));
        await db.SaveChangesAsync();

        var executor = new AccessSearchExecutor(db);
        var apps = new List<(Guid, string)> { (a1, "s1-app-1"), (a2, "s1-app-2"), (b1, "s2-app-1") };

        async Task<string> DetailAsync(string relationship, string tenantKey)
        {
            AccessSearchOutcome outcome = await executor.ExecuteGroupPlatformAsync(
                apps, new AccessSearchSpec("tenant", [], GroupByRelationship: relationship), CancellationToken.None);
            Assert.True(outcome.IsValid);
            Assert.Equal(AccessSearchModes.Group, outcome.Mode);
            return Assert.Single(outcome.Rows, r => r.DeepLinkKey == tenantKey).Detail;
        }

        Assert.Equal("3 roles", await DetailAsync("roles", "squad-1"));
        Assert.Equal("1 roles", await DetailAsync("roles", "squad-2"));
        Assert.Equal("2 permissions", await DetailAsync("permissions", "squad-1"));
        Assert.Equal("1 permissions", await DetailAsync("permissions", "squad-2"));
        Assert.Equal("3 policies", await DetailAsync("policies", "squad-1"));
        Assert.Equal("1 policies", await DetailAsync("policies", "squad-2"));
        Assert.Equal("2 assignments", await DetailAsync("assignments", "squad-1"));
        Assert.Equal("1 assignments", await DetailAsync("assignments", "squad-2"));
    }

    [Fact]
    public async Task GroupPlatform_PerTenant_RespectsAccessibleScope()
    {
        // Only squad-1's application is in scope; squad-2 must not appear and its rows must not count.
        var options = new DbContextOptionsBuilder<AuthorizationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AuthorizationDbContext(options);

        Guid s1 = Guid.NewGuid();
        Guid s2 = Guid.NewGuid();
        db.Tenants.AddRange(
            new TenantEntity { Id = s1, TenantId = "squad-1", Name = "Squad 1", Status = "ACTIVE" },
            new TenantEntity { Id = s2, TenantId = "squad-2", Name = "Squad 2", Status = "ACTIVE" });
        Guid a1 = Guid.NewGuid();
        Guid b1 = Guid.NewGuid();
        db.Applications.AddRange(
            new ApplicationEntity { Id = a1, ApplicationId = "s1-app-1", Name = "S1 App 1", TenantRefId = s1, Status = "ACTIVE" },
            new ApplicationEntity { Id = b1, ApplicationId = "s2-app-1", Name = "S2 App 1", TenantRefId = s2, Status = "ACTIVE" });
        db.Roles.AddRange(NewRole(a1, "r1"), NewRole(a1, "r2"), NewRole(b1, "rb"));
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteGroupPlatformAsync(
            [(a1, "s1-app-1")],
            new AccessSearchSpec("tenant", [], GroupByRelationship: "roles"),
            CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal("2 roles", Assert.Single(outcome.Rows, r => r.DeepLinkKey == "squad-1").Detail);
        Assert.DoesNotContain(outcome.Rows, r => r.DeepLinkKey == "squad-2");
    }

    [Fact]
    public async Task Group_RolePolicies_CountsGoverningPoliciesDeduped()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        RoleEntity editor = NewRole(a1, "editor");
        PermissionEntity granted = NewPermission(a1, "price.publish");
        PermissionEntity ungranted = NewPermission(a1, "price.view");
        db.Roles.Add(editor);
        db.Permissions.AddRange(granted, ungranted);
        // The same permission is granted through two published rows (duplicate grant); the policies on
        // it must still count once, not twice.
        db.RolePermissions.AddRange(
            NewRolePermission(a1, editor.Id, granted.Id),
            NewRolePermission(a1, editor.Id, granted.Id));
        db.Policies.AddRange(
            NewPolicy(a1, "pol-1", granted.Id),
            NewPolicy(a1, "pol-2", granted.Id),
            NewPolicy(a1, "pol-3", ungranted.Id)); // on a permission the role does not grant → excluded
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("role", [], GroupByRelationship: "policies"), CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal(AccessSearchModes.Group, outcome.Mode);
        Assert.Equal("2 policies", Assert.Single(outcome.Rows, r => r.DeepLinkKey == "editor").Detail);
    }

    [Fact]
    public async Task Group_SubjectPermissions_CountsEffectivePermissionsDeduped()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        RoleEntity r1 = NewRole(a1, "role-1");
        RoleEntity r2 = NewRole(a1, "role-2");
        PermissionEntity p1 = NewPermission(a1, "perm-1");
        PermissionEntity p2 = NewPermission(a1, "perm-2");
        PermissionEntity p3 = NewPermission(a1, "perm-3");
        db.Roles.AddRange(r1, r2);
        db.Permissions.AddRange(p1, p2, p3);
        // r1 grants {p1,p2}; r2 grants {p2,p3}. The subject holds both roles, so p2 is reachable twice
        // but the effective-permission count must be 3, not 4.
        db.RolePermissions.AddRange(
            NewRolePermission(a1, r1.Id, p1.Id), NewRolePermission(a1, r1.Id, p2.Id),
            NewRolePermission(a1, r2.Id, p2.Id), NewRolePermission(a1, r2.Id, p3.Id));
        db.Assignments.AddRange(
            NewAssignment(a1, "u@x.test", roleRefId: r1.Id),
            NewAssignment(a1, "u@x.test", roleRefId: r2.Id));
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("subject", [], GroupByRelationship: "permissions"), CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal("3 permissions", Assert.Single(outcome.Rows, r => r.DeepLinkKey == "u@x.test").Detail);
    }

    [Fact]
    public async Task Group_ApplicationSubjects_CountsDistinctActiveSubjects()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        RoleEntity role = NewRole(a1, "r");
        db.Roles.Add(role);
        db.Assignments.AddRange(
            NewAssignment(a1, "u1@x.test", roleRefId: role.Id),
            NewAssignment(a1, "u1@x.test", roleRefId: role.Id),                       // duplicate subject → counts once
            NewAssignment(a1, "u2@x.test", roleRefId: role.Id),
            NewAssignment(a1, "u3@x.test", state: "REVOKED", roleRefId: role.Id));    // inactive → excluded
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("application", [], GroupByRelationship: "subjects"), CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal("2 subjects", Assert.Single(outcome.Rows, r => r.DeepLinkKey == "app-1").Detail);
    }

    [Fact]
    public async Task ReviewCampaign_IsSearchableAndCountable()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        db.ReviewCampaigns.AddRange(
            new ReviewCampaignEntity { Id = Guid.NewGuid(), ApplicationRefId = a1, Name = "Q1 Review", Status = "ACTIVE" },
            new ReviewCampaignEntity { Id = Guid.NewGuid(), ApplicationRefId = a1, Name = "Q2 Review", Status = "DRAFT" });
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("reviewCampaign", [], Aggregate: "count"), CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal(AccessSearchModes.Count, outcome.Mode);
        Assert.Equal("2", Assert.Single(outcome.Rows).Title);
    }

    [Fact]
    public async Task FieldGroup_RolesByRiskLevel_CountsPerValue()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        db.Roles.AddRange(
            NewRole(a1, "r1", riskLevel: "HIGH"),
            NewRole(a1, "r2", riskLevel: "HIGH"),
            NewRole(a1, "r3", riskLevel: "LOW"));
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("role", [], GroupByField: "riskLevel"), CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal(AccessSearchModes.Group, outcome.Mode);
        Assert.Equal("2 roles", Assert.Single(outcome.Rows, r => r.Title == "HIGH").Detail);
        Assert.Equal("1 role", Assert.Single(outcome.Rows, r => r.Title == "LOW").Detail);
    }

    [Fact]
    public async Task FieldGroupPlatform_PoliciesByEffect_SumsAcrossApplications()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        Guid a2 = Guid.NewGuid();
        db.Applications.AddRange(
            new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" },
            new ApplicationEntity { Id = a2, ApplicationId = "app-2", Name = "App 2", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        db.Policies.AddRange(
            NewPolicy(a1, "p1", effect: "ALLOW"), NewPolicy(a1, "p2", effect: "DENY"),
            NewPolicy(a2, "p3", effect: "ALLOW"), NewPolicy(a2, "p4", effect: "ALLOW"));
        await db.SaveChangesAsync();

        // A field group-by must aggregate over the whole scope in one pass, not per application.
        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteFieldGroupPlatformAsync(
            [(a1, "app-1"), (a2, "app-2")],
            new AccessSearchSpec("policy", [], GroupByField: "effect"),
            CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal(AccessSearchModes.Group, outcome.Mode);
        Assert.Equal("3 policies", Assert.Single(outcome.Rows, r => r.Title == "ALLOW").Detail);
        Assert.Equal("1 policy", Assert.Single(outcome.Rows, r => r.Title == "DENY").Detail);
    }

    [Fact]
    public async Task FieldGroup_UnsupportedField_IsRejected()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("role", [], GroupByField: "roleKey"), CancellationToken.None);

        Assert.False(outcome.IsValid);
        Assert.Contains("roleKey", outcome.Error);
    }

    [Fact]
    public async Task GroupByRelationship_TakesPrecedenceOverGroupByField()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        RoleEntity editor = NewRole(a1, "editor", riskLevel: "HIGH");
        PermissionEntity perm = NewPermission(a1, "price.publish");
        db.Roles.Add(editor);
        db.Permissions.Add(perm);
        db.RolePermissions.Add(NewRolePermission(a1, editor.Id, perm.Id));
        await db.SaveChangesAsync();

        // Both set: the relationship group (permissions per role) must win over the field group (by risk).
        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1",
            new AccessSearchSpec("role", [], GroupByRelationship: "permissions", GroupByField: "riskLevel"),
            CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal(AccessSearchModes.Group, outcome.Mode);
        // Relationship path → a role row (with a deep link + permission count), not a "HIGH" risk bucket.
        AccessSearchRow row = Assert.Single(outcome.Rows, r => r.DeepLinkKey == "editor");
        Assert.Contains("permission", row.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outcome.Rows, r => r.Title == "HIGH");
    }

    [Fact]
    public async Task Records_RespectLimit()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        db.Roles.AddRange(
            NewRole(a1, "r1"), NewRole(a1, "r2"), NewRole(a1, "r3"), NewRole(a1, "r4"), NewRole(a1, "r5"));
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("role", [], Limit: 2), CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal(AccessSearchModes.Records, outcome.Mode);
        Assert.Equal(2, outcome.Rows.Count);
    }

    [Fact]
    public async Task FieldGroup_RespectsTopNLimit()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        db.Roles.AddRange(
            NewRole(a1, "h1", riskLevel: "HIGH"), NewRole(a1, "h2", riskLevel: "HIGH"), NewRole(a1, "h3", riskLevel: "HIGH"),
            NewRole(a1, "m1", riskLevel: "MEDIUM"), NewRole(a1, "m2", riskLevel: "MEDIUM"),
            NewRole(a1, "l1", riskLevel: "LOW"));
        await db.SaveChangesAsync();

        // Top 2 risk buckets by count: HIGH (3) and MEDIUM (2) — LOW (1) is excluded by the limit.
        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("role", [], GroupByField: "riskLevel", Limit: 2), CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal(AccessSearchModes.Group, outcome.Mode);
        Assert.Equal(2, outcome.Rows.Count);
        Assert.Contains(outcome.Rows, r => r.Title == "HIGH");
        Assert.Contains(outcome.Rows, r => r.Title == "MEDIUM");
        Assert.DoesNotContain(outcome.Rows, r => r.Title == "LOW");
    }

    [Fact]
    public async Task Absence_RolesWithNoAssignments_ReturnsOnlyUnassignedRoles()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        RoleEntity assigned = NewRole(a1, "assigned");
        RoleEntity idle = NewRole(a1, "idle");
        db.Roles.AddRange(assigned, idle);
        db.Assignments.Add(NewAssignment(a1, "u@x.io", state: "ACTIVE", roleRefId: assigned.Id));
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("role", [], AbsentRelationship: "assignments"), CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal(AccessSearchModes.Records, outcome.Mode);
        AccessSearchRow row = Assert.Single(outcome.Rows);
        Assert.Equal("idle", row.DeepLinkKey);
    }

    [Fact]
    public async Task Absence_UnusedPermissions_ReturnsOrphans()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        RoleEntity role = NewRole(a1, "r1");
        PermissionEntity used = NewPermission(a1, "used");
        PermissionEntity orphan = NewPermission(a1, "orphan");
        db.Roles.Add(role);
        db.Permissions.AddRange(used, orphan);
        db.RolePermissions.Add(NewRolePermission(a1, role.Id, used.Id, state: "PUBLISHED"));
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("permission", [], AbsentRelationship: "roles"), CancellationToken.None);

        Assert.True(outcome.IsValid);
        AccessSearchRow row = Assert.Single(outcome.Rows);
        Assert.Equal("orphan", row.DeepLinkKey);
    }

    [Fact]
    public async Task Absence_RolesGrantingNoPermissions_ReturnsOnlyRolesWithoutGrants()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        RoleEntity granting = NewRole(a1, "granting");
        RoleEntity empty = NewRole(a1, "empty");
        PermissionEntity perm = NewPermission(a1, "p1");
        db.Roles.AddRange(granting, empty);
        db.Permissions.Add(perm);
        db.RolePermissions.Add(NewRolePermission(a1, granting.Id, perm.Id, state: "PUBLISHED"));
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("role", [], AbsentRelationship: "permissions"), CancellationToken.None);

        Assert.True(outcome.IsValid);
        AccessSearchRow row = Assert.Single(outcome.Rows);
        Assert.Equal("empty", row.DeepLinkKey);
    }

    [Fact]
    public async Task Absence_SubjectEntity_IsRejected()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        await db.SaveChangesAsync();

        // subject is a derived, cross-application entity, so per-application absence could yield false
        // positives; it is rejected (guidance) rather than answered incorrectly.
        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("subject", [], AbsentRelationship: "roles"), CancellationToken.None);

        Assert.False(outcome.IsValid);
        Assert.Contains("Absence", outcome.Error);
    }

    [Fact]
    public async Task Absence_UnknownRelationship_IsRejected()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("role", [], AbsentRelationship: "bogus"), CancellationToken.None);

        Assert.False(outcome.IsValid);
        Assert.Contains("Unknown relationship", outcome.Error);
    }

    [Fact]
    public async Task NestedInclude_RolePermissionsPolicies_AttachesGrandchildren()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        RoleEntity role = NewRole(a1, "r1");
        PermissionEntity perm = NewPermission(a1, "p1");
        db.Roles.Add(role);
        db.Permissions.Add(perm);
        db.RolePermissions.Add(NewRolePermission(a1, role.Id, perm.Id, state: "PUBLISHED"));
        db.Policies.Add(NewPolicy(a1, "pol1", permissionRefId: perm.Id));
        await db.SaveChangesAsync();

        // role → permissions → policies: each role carries its permissions, and each permission
        // carries the policies attached to it.
        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("role", [], Include: "permissions", IncludeChild: "policies"), CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal(AccessSearchModes.Tree, outcome.Mode);
        AccessSearchRow roleRow = Assert.Single(outcome.Rows);
        AccessSearchRow permChild = Assert.Single(roleRow.Children!);
        Assert.Equal("p1", permChild.DeepLinkKey);
        AccessSearchRow policyGrandchild = Assert.Single(permChild.Children!);
        Assert.Equal("pol1", policyGrandchild.DeepLinkKey);
    }

    [Fact]
    public async Task NestedInclude_InvalidChildRelationship_IsRejected()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("role", [], Include: "permissions", IncludeChild: "bogus"), CancellationToken.None);

        Assert.False(outcome.IsValid);
        Assert.Contains("nested relationship", outcome.Error);
    }

    [Fact]
    public async Task NestedInclude_WithoutInclude_IsRejected()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("role", [], IncludeChild: "policies"), CancellationToken.None);

        Assert.False(outcome.IsValid);
        Assert.Contains("requires an include", outcome.Error);
    }

    [Fact]
    public async Task NestedInclude_TenantRolesPermissionsPolicies_AttachesThirdLevel()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid tenantRefId = Guid.NewGuid();
        Guid a1 = Guid.NewGuid();
        db.Tenants.Add(new TenantEntity { Id = tenantRefId, TenantId = "acme", Name = "Acme Corp", Status = "ACTIVE" });
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = tenantRefId, Status = "ACTIVE" });
        RoleEntity role = NewRole(a1, "r1");
        PermissionEntity perm = NewPermission(a1, "p1");
        db.Roles.Add(role);
        db.Permissions.Add(perm);
        db.RolePermissions.Add(NewRolePermission(a1, role.Id, perm.Id, state: "PUBLISHED"));
        db.Policies.Add(NewPolicy(a1, "pol1", permissionRefId: perm.Id));
        await db.SaveChangesAsync();

        // tenant → roles → permissions → policies: each tenant carries its roles, each role its
        // permissions, and each permission the policies attached to it (a third nested level).
        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1",
            new AccessSearchSpec("tenant", [], Include: "roles", IncludeChild: "permissions", IncludeGrandchild: "policies"),
            CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal(AccessSearchModes.Tree, outcome.Mode);
        AccessSearchRow tenantRow = Assert.Single(outcome.Rows);
        AccessSearchRow roleChild = Assert.Single(tenantRow.Children!);
        Assert.Equal("r1", roleChild.DeepLinkKey);
        AccessSearchRow permGrandchild = Assert.Single(roleChild.Children!);
        Assert.Equal("p1", permGrandchild.DeepLinkKey);
        AccessSearchRow policyGreatGrandchild = Assert.Single(permGrandchild.Children!);
        Assert.Equal("pol1", policyGreatGrandchild.DeepLinkKey);
    }

    [Fact]
    public async Task NestedGrandchildInclude_InvalidRelationship_IsRejected()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1",
            new AccessSearchSpec("role", [], Include: "permissions", IncludeChild: "policies", IncludeGrandchild: "bogus"),
            CancellationToken.None);

        Assert.False(outcome.IsValid);
        Assert.Contains("nested relationship", outcome.Error);
    }

    [Fact]
    public async Task NestedGrandchildInclude_WithoutIncludeChild_IsRejected()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1",
            new AccessSearchSpec("role", [], Include: "permissions", IncludeGrandchild: "policies"),
            CancellationToken.None);

        Assert.False(outcome.IsValid);
        Assert.Contains("requires includeChild", outcome.Error);
    }

    [Fact]
    public async Task Compound_RolesWithPermissionsButNoAssignments_ReturnsOnlyUnassigned()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        RoleEntity assignedRole = NewRole(a1, "r-assigned");
        RoleEntity idleRole = NewRole(a1, "r-idle");
        PermissionEntity perm = NewPermission(a1, "p1");
        db.Roles.AddRange(assignedRole, idleRole);
        db.Permissions.Add(perm);
        // Both roles grant a permission; only the first has an active assignment.
        db.RolePermissions.AddRange(
            NewRolePermission(a1, assignedRole.Id, perm.Id, state: "PUBLISHED"),
            NewRolePermission(a1, idleRole.Id, perm.Id, state: "PUBLISHED"));
        db.Assignments.Add(NewAssignment(a1, "u@x.io", state: "ACTIVE", roleRefId: assignedRole.Id));
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("role", [], PresentRelationship: "permissions", AbsentRelationship: "assignments"), CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal(AccessSearchModes.Records, outcome.Mode);
        AccessSearchRow row = Assert.Single(outcome.Rows);
        Assert.Equal("r-idle", row.DeepLinkKey);
    }

    [Fact]
    public async Task Compound_PermissionsWithRolesButNoPolicies_ReturnsOnlyUngoverned()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        RoleEntity role = NewRole(a1, "r1");
        PermissionEntity governed = NewPermission(a1, "p-gov");
        PermissionEntity ungoverned = NewPermission(a1, "p-ungov");
        db.Roles.Add(role);
        db.Permissions.AddRange(governed, ungoverned);
        // Both permissions are granted by a role; only the first has a policy attached.
        db.RolePermissions.AddRange(
            NewRolePermission(a1, role.Id, governed.Id, state: "PUBLISHED"),
            NewRolePermission(a1, role.Id, ungoverned.Id, state: "PUBLISHED"));
        db.Policies.Add(NewPolicy(a1, "pol", permissionRefId: governed.Id));
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("permission", [], PresentRelationship: "roles", AbsentRelationship: "policies"), CancellationToken.None);

        Assert.True(outcome.IsValid);
        AccessSearchRow row = Assert.Single(outcome.Rows);
        Assert.Equal("p-ungov", row.DeepLinkKey);
    }

    [Fact]
    public async Task Presence_RolesThatHaveAssignments_ReturnsOnlyAssigned()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        RoleEntity assigned = NewRole(a1, "r-has");
        RoleEntity idle = NewRole(a1, "r-none");
        db.Roles.AddRange(assigned, idle);
        db.Assignments.Add(NewAssignment(a1, "u@x.io", state: "ACTIVE", roleRefId: assigned.Id));
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("role", [], PresentRelationship: "assignments"), CancellationToken.None);

        Assert.True(outcome.IsValid);
        AccessSearchRow row = Assert.Single(outcome.Rows);
        Assert.Equal("r-has", row.DeepLinkKey);
    }

    [Fact]
    public async Task Presence_UnknownRelationship_IsRejected()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("role", [], PresentRelationship: "bogus"), CancellationToken.None);

        Assert.False(outcome.IsValid);
        Assert.Contains("Unknown relationship", outcome.Error);
    }

    [Fact]
    public async Task Presence_SubjectEntity_IsRejected()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("subject", [], PresentRelationship: "roles"), CancellationToken.None);

        Assert.False(outcome.IsValid);
        Assert.Contains("Presence", outcome.Error);
    }

    [Fact]
    public async Task PresenceAbsencePlatform_TenantsWithPoliciesButNoActiveUsers_ReturnsOnlyUnused()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid t1 = Guid.NewGuid();
        Guid t2 = Guid.NewGuid();
        db.Tenants.AddRange(
            new TenantEntity { Id = t1, TenantId = "squad-1", Name = "Squad 1", Status = "ACTIVE" },
            new TenantEntity { Id = t2, TenantId = "squad-2", Name = "Squad 2", Status = "ACTIVE" });
        Guid a1 = Guid.NewGuid();
        Guid a2 = Guid.NewGuid();
        db.Applications.AddRange(
            new ApplicationEntity { Id = a1, ApplicationId = "s1-app", Name = "S1 App", TenantRefId = t1, Status = "ACTIVE" },
            new ApplicationEntity { Id = a2, ApplicationId = "s2-app", Name = "S2 App", TenantRefId = t2, Status = "ACTIVE" });
        // Both tenants have a policy; only squad-1 has an active assignment.
        db.Policies.AddRange(NewPolicy(a1, "pol1"), NewPolicy(a2, "pol2"));
        db.Assignments.Add(NewAssignment(a1, "u1@x.test", state: "ACTIVE"));
        await db.SaveChangesAsync();

        var apps = new List<(Guid, string)> { (a1, "s1-app"), (a2, "s2-app") };
        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecutePresenceAbsencePlatformAsync(
            apps, new AccessSearchSpec("tenant", [], PresentRelationship: "policies", AbsentRelationship: "assignments"), CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal(AccessSearchModes.Records, outcome.Mode);
        AccessSearchRow row = Assert.Single(outcome.Rows);
        Assert.Equal("squad-2", row.DeepLinkKey);
    }

    [Fact]
    public async Task PresenceAbsencePlatform_EvaluatesUsersAcrossAllTenantApps()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid t1 = Guid.NewGuid();
        db.Tenants.Add(new TenantEntity { Id = t1, TenantId = "squad-1", Name = "Squad 1", Status = "ACTIVE" });
        Guid a1 = Guid.NewGuid();
        Guid a2 = Guid.NewGuid();
        db.Applications.AddRange(
            new ApplicationEntity { Id = a1, ApplicationId = "s1-app-1", Name = "S1 App 1", TenantRefId = t1, Status = "ACTIVE" },
            new ApplicationEntity { Id = a2, ApplicationId = "s1-app-2", Name = "S1 App 2", TenantRefId = t1, Status = "ACTIVE" });
        // Policy in app-1; the tenant's only active user is in app-2. A per-application evaluation of
        // app-1 alone would wrongly report "no active users"; the single pass sees the app-2 assignment.
        db.Policies.Add(NewPolicy(a1, "pol1"));
        db.Assignments.Add(NewAssignment(a2, "u1@x.test", state: "ACTIVE"));
        await db.SaveChangesAsync();

        var apps = new List<(Guid, string)> { (a1, "s1-app-1"), (a2, "s1-app-2") };
        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecutePresenceAbsencePlatformAsync(
            apps, new AccessSearchSpec("tenant", [], PresentRelationship: "policies", AbsentRelationship: "assignments"), CancellationToken.None);

        Assert.True(outcome.IsValid);
        // squad-1 HAS an active user (in app-2), so it must NOT be reported as "no active users".
        Assert.Empty(outcome.Rows);
    }

    [Fact]
    public async Task PresenceAbsencePlatform_UnknownRelationship_IsRejected()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid t1 = Guid.NewGuid();
        db.Tenants.Add(new TenantEntity { Id = t1, TenantId = "squad-1", Name = "Squad 1", Status = "ACTIVE" });
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "s1-app", Name = "S1 App", TenantRefId = t1, Status = "ACTIVE" });
        await db.SaveChangesAsync();

        var apps = new List<(Guid, string)> { (a1, "s1-app") };
        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecutePresenceAbsencePlatformAsync(
            apps, new AccessSearchSpec("tenant", [], PresentRelationship: "bogus"), CancellationToken.None);

        Assert.False(outcome.IsValid);
        Assert.Contains("Unknown relationship", outcome.Error);
    }

    [Fact]
    public async Task FieldGroupNested_RolesByRiskThenStatus_NestsSecondaryUnderPrimary()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        db.Roles.AddRange(
            NewRole(a1, "h1", status: "ACTIVE", riskLevel: "HIGH"),
            NewRole(a1, "h2", status: "ACTIVE", riskLevel: "HIGH"),
            NewRole(a1, "h3", status: "DISABLED", riskLevel: "HIGH"),
            NewRole(a1, "m1", status: "ACTIVE", riskLevel: "MEDIUM"));
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("role", [], GroupByField: "riskLevel", GroupBySecondaryField: "status"), CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal(AccessSearchModes.Group, outcome.Mode);
        Assert.Equal(2, outcome.Rows.Count);

        AccessSearchRow high = outcome.Rows[0];
        Assert.Equal("HIGH", high.Title);
        Assert.Contains("3", high.Detail);
        Assert.Equal(2, high.Children!.Count);
        Assert.Contains("2", Assert.Single(high.Children!, c => c.Title == "ACTIVE").Detail);
        Assert.Contains("1", Assert.Single(high.Children!, c => c.Title == "DISABLED").Detail);

        AccessSearchRow medium = outcome.Rows[1];
        Assert.Equal("MEDIUM", medium.Title);
        Assert.Equal("ACTIVE", Assert.Single(medium.Children!).Title);
    }

    [Fact]
    public async Task FieldGroupNested_SecondaryWithoutPrimary_IsRejected()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("role", [], GroupBySecondaryField: "status"), CancellationToken.None);

        Assert.False(outcome.IsValid);
        Assert.Contains("requires a primary", outcome.Error);
    }

    [Fact]
    public async Task FieldGroupNested_SamePrimaryAndSecondary_IsRejected()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        db.Applications.Add(new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteAsync(
            a1, "app-1", new AccessSearchSpec("role", [], GroupByField: "riskLevel", GroupBySecondaryField: "riskLevel"), CancellationToken.None);

        Assert.False(outcome.IsValid);
        Assert.Contains("must differ", outcome.Error);
    }

    [Fact]
    public async Task Presence_ApplicationsWithUnusedPermissions_ReturnsOnlyThoseWithOrphans()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        Guid a2 = Guid.NewGuid();
        db.Applications.AddRange(
            new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" },
            new ApplicationEntity { Id = a2, ApplicationId = "app-2", Name = "App 2", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        RoleEntity r1 = NewRole(a1, "r1");
        RoleEntity r2 = NewRole(a2, "r2");
        PermissionEntity orphan = NewPermission(a1, "p-orphan");
        PermissionEntity used1 = NewPermission(a1, "p-used");
        PermissionEntity used2 = NewPermission(a2, "p2-used");
        db.Roles.AddRange(r1, r2);
        db.Permissions.AddRange(orphan, used1, used2);
        // app-1 has one orphan permission (p-orphan); app-2's only permission is granted.
        db.RolePermissions.AddRange(
            NewRolePermission(a1, r1.Id, used1.Id, state: "PUBLISHED"),
            NewRolePermission(a2, r2.Id, used2.Id, state: "PUBLISHED"));
        await db.SaveChangesAsync();

        var apps = new List<(Guid, string)> { (a1, "app-1"), (a2, "app-2") };
        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecutePresenceAbsencePlatformAsync(
            apps, new AccessSearchSpec("application", [], PresentRelationship: "unusedPermissions"), CancellationToken.None);

        Assert.True(outcome.IsValid);
        AccessSearchRow row = Assert.Single(outcome.Rows);
        Assert.Equal("app-1", row.DeepLinkKey);
    }

    [Fact]
    public async Task Group_UnusedPermissionsPerApplication_CountsOnlyAppsWithOrphans()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        Guid a2 = Guid.NewGuid();
        db.Applications.AddRange(
            new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" },
            new ApplicationEntity { Id = a2, ApplicationId = "app-2", Name = "App 2", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        RoleEntity r2 = NewRole(a2, "r2");
        PermissionEntity orphanA = NewPermission(a1, "p-a1");
        PermissionEntity orphanB = NewPermission(a1, "p-a2");
        PermissionEntity used2 = NewPermission(a2, "p2-used");
        db.Roles.Add(r2);
        db.Permissions.AddRange(orphanA, orphanB, used2);
        db.RolePermissions.Add(NewRolePermission(a2, r2.Id, used2.Id, state: "PUBLISHED"));
        await db.SaveChangesAsync();

        var apps = new List<(Guid, string)> { (a1, "app-1"), (a2, "app-2") };
        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecuteGroupPlatformAsync(
            apps, new AccessSearchSpec("application", [], GroupByRelationship: "unusedPermissions"), CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal(AccessSearchModes.Group, outcome.Mode);
        // app-1 has 2 unused permissions; app-2 has none, so it is not in the count list.
        Assert.Contains("2", Assert.Single(outcome.Rows, r => r.DeepLinkKey == "app-1").Detail);
        Assert.DoesNotContain(outcome.Rows, r => r.DeepLinkKey == "app-2");
    }

    [Fact]
    public async Task Presence_ApplicationsWithUnassignedRoles_ReturnsOnlyThoseWithIdleRoles()
    {
        await using AuthorizationDbContext db = NewContext();
        Guid a1 = Guid.NewGuid();
        Guid a2 = Guid.NewGuid();
        db.Applications.AddRange(
            new ApplicationEntity { Id = a1, ApplicationId = "app-1", Name = "App 1", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" },
            new ApplicationEntity { Id = a2, ApplicationId = "app-2", Name = "App 2", TenantRefId = Guid.NewGuid(), Status = "ACTIVE" });
        RoleEntity idle = NewRole(a1, "r-idle");
        RoleEntity active1 = NewRole(a1, "r-active");
        RoleEntity active2 = NewRole(a2, "r2");
        db.Roles.AddRange(idle, active1, active2);
        // app-1 has an idle role (r-idle, no active assignment); every app-2 role is assigned.
        db.Assignments.AddRange(
            NewAssignment(a1, "u1@x.io", state: "ACTIVE", roleRefId: active1.Id),
            NewAssignment(a2, "u2@x.io", state: "ACTIVE", roleRefId: active2.Id));
        await db.SaveChangesAsync();

        var apps = new List<(Guid, string)> { (a1, "app-1"), (a2, "app-2") };
        AccessSearchOutcome outcome = await new AccessSearchExecutor(db).ExecutePresenceAbsencePlatformAsync(
            apps, new AccessSearchSpec("application", [], PresentRelationship: "unassignedRoles"), CancellationToken.None);

        Assert.True(outcome.IsValid);
        AccessSearchRow row = Assert.Single(outcome.Rows);
        Assert.Equal("app-1", row.DeepLinkKey);
    }

    private static RoleEntity NewRole(Guid appRefId, string key, string status = "ACTIVE", string riskLevel = "MEDIUM") =>
        new() { Id = Guid.NewGuid(), ApplicationRefId = appRefId, RoleKey = key, Name = key, Status = status, RiskLevel = riskLevel };

    private static PermissionEntity NewPermission(Guid appRefId, string key, string status = "ACTIVE") =>
        new() { Id = Guid.NewGuid(), ApplicationRefId = appRefId, PermissionKey = key, Resource = key, Action = "read", Status = status };

    private static PolicyEntity NewPolicy(Guid appRefId, string key, Guid? permissionRefId = null, string effect = "ALLOW") =>
        new() { Id = Guid.NewGuid(), ApplicationRefId = appRefId, PolicyKey = key, PermissionRefId = permissionRefId ?? Guid.NewGuid(), Effect = effect, Conditions = "{}" };

    private static AssignmentEntity NewAssignment(Guid appRefId, string email, string state = "ACTIVE", Guid? roleRefId = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            ApplicationRefId = appRefId,
            SubjectType = "USER",
            SubjectEmail = email,
            RoleRefId = roleRefId ?? Guid.NewGuid(),
            ValidFrom = DateTimeOffset.UtcNow,
            State = state,
        };

    private static RolePermissionEntity NewRolePermission(Guid appRefId, Guid roleId, Guid permissionId, string state = "PUBLISHED") =>
        new() { Id = Guid.NewGuid(), ApplicationRefId = appRefId, RoleRefId = roleId, PermissionRefId = permissionId, State = state };

    private static AuthorizationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AuthorizationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task RecordRows_CarryReadableApplicationAndTenantContext()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("role", []));

        Assert.True(outcome.IsValid);
        // Every row is stamped with the readable application and tenant names so the UI can show
        // "which application / which tenant" instead of a bare id.
        Assert.All(outcome.Rows, row =>
        {
            Assert.Equal("Pricing Management", row.ApplicationName);
            Assert.Equal("Pricing Org", row.TenantName);
            Assert.Equal(ApplicationId, row.ApplicationId);
        });
    }

    [Fact]
    public async Task RoleQuery_StatusIn_ReturnsAnyListedStatus()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();
        db.Roles.Add(RoleWithStatus("legacy-a", "DISABLED"));
        db.Roles.Add(RoleWithStatus("legacy-b", "ARCHIVED"));
        db.Roles.Add(RoleWithStatus("legacy-c", "DEPRECATED"));
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("role",
            [new AccessSearchFilter("status", "in", "disabled,archived")]));

        Assert.True(outcome.IsValid);
        Assert.Equal(2, outcome.Rows.Count);
        Assert.DoesNotContain(outcome.Rows, r => r.DeepLinkKey == "legacy-c");
    }

    [Fact]
    public async Task AssignmentQuery_StateExpired_ReturnsOnlyExpired()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();
        Guid roleId = db.Roles.First().Id;
        db.Assignments.Add(AssignmentWith(roleId, "user9.temp@icis.com", "EXPIRED", DateTimeOffset.UtcNow.AddDays(-2)));
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("assignment",
            [new AccessSearchFilter("state", "eq", "EXPIRED")]));

        Assert.True(outcome.IsValid);
        Assert.Contains(outcome.Rows, r => r.DeepLinkKey == "user9.temp@icis.com");
        Assert.DoesNotContain(outcome.Rows, r => r.DeepLinkKey == "user7.lead@icis.com");
    }

    [Fact]
    public async Task AssignmentQuery_ValidUntilBeforeNow_ReturnsPastExpiry()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();
        Guid roleId = db.Roles.First().Id;
        db.Assignments.Add(AssignmentWith(roleId, "past@icis.com", "EXPIRED", DateTimeOffset.UtcNow.AddDays(-1)));
        db.Assignments.Add(AssignmentWith(roleId, "future@icis.com", "ACTIVE", DateTimeOffset.UtcNow.AddDays(30)));
        await db.SaveChangesAsync();

        AccessSearchOutcome outcome = await Run(db, new AccessSearchSpec("assignment",
            [new AccessSearchFilter("validUntil", "lt", "now")]));

        Assert.True(outcome.IsValid);
        Assert.Contains(outcome.Rows, r => r.DeepLinkKey == "past@icis.com");
        Assert.DoesNotContain(outcome.Rows, r => r.DeepLinkKey == "future@icis.com");
    }

    private Task<AccessSearchOutcome> Run(AuthorizationDbContext db, AccessSearchSpec spec) =>
        new AccessSearchExecutor(db).ExecuteAsync(appId, ApplicationId, spec, CancellationToken.None);

    [Fact]
    public async Task Vocabulary_IsRelevanceScoped_ToQuestionKeywords()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();

        AccessSearchVocabulary vocab = await new AccessSearchExecutor(db)
            .BuildRelevantVocabularyAsync([appId], "which roles grant the publish action", CancellationToken.None);

        // "publish" is a keyword, so the publish action must surface; "view"/"edit" are not mentioned.
        Assert.Contains("publish", vocab.Actions);
        Assert.DoesNotContain("view", vocab.Actions);
        Assert.DoesNotContain("edit", vocab.Actions);
    }

    [Fact]
    public async Task Vocabulary_FallsBackToBaseline_WhenNoKeywordMatches()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();

        AccessSearchVocabulary vocab = await new AccessSearchExecutor(db)
            .BuildRelevantVocabularyAsync([appId], "completely unrelated gibberish tokens", CancellationToken.None);

        // No keyword overlaps the catalog, so a small baseline sample is returned so the model still
        // sees the naming pattern rather than an empty list.
        Assert.NotEmpty(vocab.PermissionKeys);
        Assert.NotEmpty(vocab.Actions);
    }

    [Fact]
    public async Task ResolveFilterValues_CanonicalisesEnumCase()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();

        AccessSearchValueResolution resolution = await new AccessSearchExecutor(db).ResolveFilterValuesAsync(
            [appId],
            new AccessSearchSpec("role", [new AccessSearchFilter("status", "eq", "active")]),
            CancellationToken.None);

        Assert.True(resolution.Success);
        Assert.Equal("ACTIVE", resolution.Spec!.Filters[0].Value);
    }

    [Fact]
    public async Task ResolveFilterValues_RejectsUnknownEnum_WithAllowedValues()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();

        AccessSearchValueResolution resolution = await new AccessSearchExecutor(db).ResolveFilterValuesAsync(
            [appId],
            new AccessSearchSpec("role", [new AccessSearchFilter("status", "eq", "banana")]),
            CancellationToken.None);

        Assert.False(resolution.Success);
        Assert.Contains("ACTIVE", resolution.Error);
    }

    [Fact]
    public async Task ResolveFilterValues_ResolvesRefExact_CaseInsensitive()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();

        AccessSearchValueResolution resolution = await new AccessSearchExecutor(db).ResolveFilterValuesAsync(
            [appId],
            new AccessSearchSpec("role", [new AccessSearchFilter("roleKey", "eq", "PRICING-LEAD")]),
            CancellationToken.None);

        Assert.True(resolution.Success);
        Assert.Equal("pricing-lead", resolution.Spec!.Filters[0].Value);
    }

    [Fact]
    public async Task ResolveFilterValues_ResolvesRefFuzzy_WhenUnambiguous()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();

        // "publ" only matches the "publish" action, so it resolves to the real value.
        AccessSearchValueResolution resolution = await new AccessSearchExecutor(db).ResolveFilterValuesAsync(
            [appId],
            new AccessSearchSpec("role", [new AccessSearchFilter("action", "eq", "publ")]),
            CancellationToken.None);

        Assert.True(resolution.Success);
        Assert.Equal("publish", resolution.Spec!.Filters[0].Value);
    }

    [Fact]
    public async Task ResolveFilterValues_AmbiguousRef_ReturnsDidYouMeanGuidance()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();

        // "pricing" matches every seeded role key, so the value cannot be resolved confidently.
        AccessSearchValueResolution resolution = await new AccessSearchExecutor(db).ResolveFilterValuesAsync(
            [appId],
            new AccessSearchSpec("role", [new AccessSearchFilter("roleKey", "eq", "pricing")]),
            CancellationToken.None);

        Assert.False(resolution.Success);
        Assert.Contains("Did you mean", resolution.Error);
    }

    [Fact]
    public async Task ResolveFilterValues_UnknownRef_ReturnsNoMatchGuidance()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();

        AccessSearchValueResolution resolution = await new AccessSearchExecutor(db).ResolveFilterValuesAsync(
            [appId],
            new AccessSearchSpec("role", [new AccessSearchFilter("roleKey", "eq", "does-not-exist")]),
            CancellationToken.None);

        Assert.False(resolution.Success);
        Assert.Contains("No roleKey matching", resolution.Error);
    }

    [Fact]
    public async Task ResolveFilterValues_ContainsOperator_IsPassedThroughUnchanged()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync();

        AccessSearchValueResolution resolution = await new AccessSearchExecutor(db).ResolveFilterValuesAsync(
            [appId],
            new AccessSearchSpec("role", [new AccessSearchFilter("roleKey", "contains", "PRICING")]),
            CancellationToken.None);

        Assert.True(resolution.Success);
        Assert.Equal("PRICING", resolution.Spec!.Filters[0].Value);
    }

    private AuthorizationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AuthorizationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AuthorizationDbContext(options);
        db.Tenants.Add(new TenantEntity
        {
            Id = tenantRefId,
            TenantId = "pricing-org",
            Name = "Pricing Org",
            Status = "ACTIVE",
        });
        db.Applications.Add(new ApplicationEntity
        {
            Id = appId,
            ApplicationId = ApplicationId,
            Name = "Pricing Management",
            TenantRefId = tenantRefId,
            Status = "ACTIVE",
        });
        db.SaveChanges();
        return db;
    }

    // Seeds a compact version of the pricing-management fixture used across F5/F6/F8 tests.
    private async Task<AuthorizationDbContext> SeedPricingAsync()
    {
        AuthorizationDbContext db = CreateContext();

        Guid analyst = Guid.NewGuid();
        Guid editor = Guid.NewGuid();
        Guid lead = Guid.NewGuid();
        Guid admin = Guid.NewGuid();
        db.Roles.AddRange(
            Role(analyst, "pricing-analyst", privileged: false),
            Role(editor, "pricing-editor", privileged: false),
            Role(lead, "pricing-lead", privileged: true),
            Role(admin, "pricing-admin", privileged: true));

        Guid view = Guid.NewGuid();
        Guid edit = Guid.NewGuid();
        Guid publish = Guid.NewGuid();
        db.Permissions.AddRange(
            Permission(view, "price.view", "price", "view"),
            Permission(edit, "price.edit", "price", "edit"),
            Permission(publish, "price.publish", "price", "publish"));

        db.RolePermissions.AddRange(
            RolePermission(analyst, view),
            RolePermission(analyst, edit),
            RolePermission(lead, view),
            RolePermission(lead, publish),
            RolePermission(admin, view),
            RolePermission(admin, edit),
            RolePermission(admin, publish));

        db.Assignments.AddRange(
            ActiveAssignment(lead, "user7.lead@icis.com"),
            RevokedAssignment(admin, "user8.admin@icis.com"));

        db.Policies.AddRange(
            Policy("allow-publish-ready", publish, "ALLOW"),
            Policy("deny-self-publish", publish, "DENY"));

        await db.SaveChangesAsync();
        return db;
    }

    private RoleEntity Role(Guid id, string key, bool privileged) => new()
    {
        Id = id,
        ApplicationRefId = appId,
        RoleKey = key,
        Name = key,
        Privileged = privileged,
        RiskLevel = privileged ? "HIGH" : "MEDIUM",
        Status = "ACTIVE",
    };

    private RoleEntity RoleWithStatus(string key, string status) => new()
    {
        Id = Guid.NewGuid(),
        ApplicationRefId = appId,
        RoleKey = key,
        Name = key,
        Privileged = false,
        RiskLevel = "LOW",
        Status = status,
    };

    private PermissionEntity Permission(Guid id, string key, string resource, string action) => new()
    {
        Id = id,
        ApplicationRefId = appId,
        PermissionKey = key,
        Resource = resource,
        Action = action,
        RiskLevel = "MEDIUM",
        Status = "ACTIVE",
    };

    private RolePermissionEntity RolePermission(Guid roleId, Guid permissionId) => new()
    {
        Id = Guid.NewGuid(),
        ApplicationRefId = appId,
        RoleRefId = roleId,
        PermissionRefId = permissionId,
        State = "PUBLISHED",
    };

    private AssignmentEntity ActiveAssignment(Guid roleId, string email) => new()
    {
        Id = Guid.NewGuid(),
        ApplicationRefId = appId,
        SubjectType = "USER",
        SubjectEmail = email,
        RoleRefId = roleId,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-10),
        State = "ACTIVE",
    };

    private AssignmentEntity RevokedAssignment(Guid roleId, string email) => new()
    {
        Id = Guid.NewGuid(),
        ApplicationRefId = appId,
        SubjectType = "USER",
        SubjectEmail = email,
        RoleRefId = roleId,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-10),
        State = "REVOKED",
        RevokedAt = DateTimeOffset.UtcNow.AddDays(-1),
    };

    private AssignmentEntity AssignmentWith(Guid roleId, string email, string state, DateTimeOffset? validUntil) => new()
    {
        Id = Guid.NewGuid(),
        ApplicationRefId = appId,
        SubjectType = "USER",
        SubjectEmail = email,
        RoleRefId = roleId,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-30),
        State = state,
        ValidUntil = validUntil,
    };

    private PolicyEntity Policy(string key, Guid permissionId, string effect) => new()
    {
        Id = Guid.NewGuid(),
        ApplicationRefId = appId,
        PolicyKey = key,
        PermissionRefId = permissionId,
        Effect = effect,
        Conditions = "{\"conditions\":[]}",
        State = "PUBLISHED",
    };
}
