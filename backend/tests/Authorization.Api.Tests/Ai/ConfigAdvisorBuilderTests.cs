using Authorization.Api.Ai;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Tests.Ai;

/// <summary>
/// Unit tests for <see cref="ConfigAdvisorBuilder"/>. Each test seeds a specific configuration
/// smell into an in-memory store and asserts the deterministic finding it produces (and that clean
/// configurations produce none). The builder is read-only, so no persistence side effects are
/// expected.
/// </summary>
public sealed class ConfigAdvisorBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 13, 0, 0, 0, TimeSpan.Zero);
    private const string ApplicationId = "app-under-test";

    private readonly Guid appId = Guid.NewGuid();

    [Fact]
    public async Task OrphanPermission_IsFlagged()
    {
        await using AuthorizationDbContext db = CreateContext();
        db.Permissions.Add(Permission("price.export"));
        await db.SaveChangesAsync();

        IReadOnlyList<ConfigFinding> findings = await Run(db);

        Assert.Contains(findings, finding => finding.Kind == "ORPHAN_PERMISSION" && finding.EntityKey == "price.export");
    }

    [Fact]
    public async Task GrantedPermission_IsNotOrphan()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid roleId = Guid.NewGuid();
        Guid permissionId = Guid.NewGuid();
        db.Roles.Add(Role(roleId, "viewer", privileged: false));
        db.Permissions.Add(Permission("price.view", permissionId));
        db.RolePermissions.Add(RolePermission(roleId, permissionId, "PUBLISHED"));
        db.Assignments.Add(ActiveAssignment(roleId));
        await db.SaveChangesAsync();

        IReadOnlyList<ConfigFinding> findings = await Run(db);

        Assert.DoesNotContain(findings, finding => finding.Kind == "ORPHAN_PERMISSION");
    }

    [Fact]
    public async Task UnusedPrivilegedRole_IsFlaggedMedium()
    {
        await using AuthorizationDbContext db = CreateContext();
        db.Roles.Add(Role(Guid.NewGuid(), "pricing-admin", privileged: true));
        await db.SaveChangesAsync();

        IReadOnlyList<ConfigFinding> findings = await Run(db);

        ConfigFinding finding = Assert.Single(findings, item => item.Kind == "UNUSED_ROLE" && item.EntityKey == "pricing-admin");
        Assert.Equal("MEDIUM", finding.Severity);
    }

    [Fact]
    public async Task RoleWithActiveAssignment_IsNotUnused()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid roleId = Guid.NewGuid();
        db.Roles.Add(Role(roleId, "viewer", privileged: false));
        db.Assignments.Add(ActiveAssignment(roleId));
        await db.SaveChangesAsync();

        IReadOnlyList<ConfigFinding> findings = await Run(db);

        Assert.DoesNotContain(findings, finding => finding.Kind == "UNUSED_ROLE");
    }

    [Fact]
    public async Task RoleWithOnlyRevokedAssignment_IsUnused()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid roleId = Guid.NewGuid();
        db.Roles.Add(Role(roleId, "viewer", privileged: false));
        db.Assignments.Add(new AssignmentEntity
        {
            Id = Guid.NewGuid(),
            ApplicationRefId = appId,
            SubjectType = "USER",
            SubjectEmail = "revoked@icis.com",
            RoleRefId = roleId,
            ValidFrom = Now.AddDays(-10),
            State = "REVOKED",
            RevokedAt = Now.AddDays(-1),
        });
        await db.SaveChangesAsync();

        IReadOnlyList<ConfigFinding> findings = await Run(db);

        Assert.Contains(findings, finding => finding.Kind == "UNUSED_ROLE" && finding.EntityKey == "viewer");
    }

    [Fact]
    public async Task PrivilegedRoleWithNoPolicy_IsFlaggedUnguarded()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid roleId = Guid.NewGuid();
        Guid permissionId = Guid.NewGuid();
        db.Roles.Add(Role(roleId, "pricing-lead", privileged: true));
        db.Permissions.Add(Permission("price.publish", permissionId));
        db.RolePermissions.Add(RolePermission(roleId, permissionId, "PUBLISHED"));
        db.Assignments.Add(ActiveAssignment(roleId));
        await db.SaveChangesAsync();

        IReadOnlyList<ConfigFinding> findings = await Run(db);

        ConfigFinding finding = Assert.Single(findings, item => item.Kind == "PRIVILEGED_UNGUARDED" && item.EntityKey == "pricing-lead");
        Assert.Equal("HIGH", finding.Severity);
    }

    [Fact]
    public async Task PrivilegedRoleWithGuardingPolicy_IsNotUnguarded()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid roleId = Guid.NewGuid();
        Guid permissionId = Guid.NewGuid();
        db.Roles.Add(Role(roleId, "pricing-lead", privileged: true));
        db.Permissions.Add(Permission("price.publish", permissionId));
        db.RolePermissions.Add(RolePermission(roleId, permissionId, "PUBLISHED"));
        db.Assignments.Add(ActiveAssignment(roleId));
        db.Policies.Add(Policy("allow-publish", permissionId, "ALLOW", "{\"conditions\":[{\"attribute\":\"context.status\",\"operator\":\"eq\",\"value\":\"READY\"}]}", "PUBLISHED"));
        await db.SaveChangesAsync();

        IReadOnlyList<ConfigFinding> findings = await Run(db);

        Assert.DoesNotContain(findings, finding => finding.Kind == "PRIVILEGED_UNGUARDED");
    }

    [Fact]
    public async Task ContextAttributeNeverSeenInDecisions_IsFlaggedDead()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid permissionId = Guid.NewGuid();
        db.Permissions.Add(Permission("price.publish", permissionId));
        db.Policies.Add(Policy("allow-publish", permissionId, "ALLOW", "{\"conditions\":[{\"attribute\":\"context.status\",\"operator\":\"eq\",\"value\":\"READY\"}]}", "PUBLISHED"));
        db.Decisions.Add(Decision("{\"region\":\"EU\"}"));
        await db.SaveChangesAsync();

        IReadOnlyList<ConfigFinding> findings = await Run(db);

        Assert.Contains(findings, finding => finding.Kind == "DEAD_CONTEXT_ATTRIBUTE" && finding.Id.Contains("status"));
    }

    [Fact]
    public async Task ContextAttributeSeenInDecisions_IsNotDead()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid permissionId = Guid.NewGuid();
        db.Permissions.Add(Permission("price.publish", permissionId));
        db.Policies.Add(Policy("allow-publish", permissionId, "ALLOW", "{\"conditions\":[{\"attribute\":\"context.status\",\"operator\":\"eq\",\"value\":\"READY\"}]}", "PUBLISHED"));
        db.Decisions.Add(Decision("{\"status\":\"READY\"}"));
        await db.SaveChangesAsync();

        IReadOnlyList<ConfigFinding> findings = await Run(db);

        Assert.DoesNotContain(findings, finding => finding.Kind == "DEAD_CONTEXT_ATTRIBUTE");
    }

    [Fact]
    public async Task ContextAttributeWithNoDecisionHistory_IsNotFlaggedDead()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid permissionId = Guid.NewGuid();
        db.Permissions.Add(Permission("price.publish", permissionId));
        db.Policies.Add(Policy("allow-publish", permissionId, "ALLOW", "{\"conditions\":[{\"attribute\":\"context.status\",\"operator\":\"eq\",\"value\":\"READY\"}]}", "PUBLISHED"));
        await db.SaveChangesAsync();

        IReadOnlyList<ConfigFinding> findings = await Run(db);

        Assert.DoesNotContain(findings, finding => finding.Kind == "DEAD_CONTEXT_ATTRIBUTE");
    }

    [Fact]
    public async Task OldDraftPolicy_IsFlaggedStale()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid permissionId = Guid.NewGuid();
        db.Permissions.Add(Permission("price.publish", permissionId));
        PolicyEntity draft = Policy("wip-policy", permissionId, "ALLOW", "{\"conditions\":[]}", "DRAFT");
        draft.CreatedAt = Now.AddDays(-30);
        db.Policies.Add(draft);
        await db.SaveChangesAsync();

        IReadOnlyList<ConfigFinding> findings = await Run(db);

        Assert.Contains(findings, finding => finding.Kind == "STALE_DRAFT" && finding.EntityKey == "wip-policy");
    }

    [Fact]
    public async Task RecentDraftPolicy_IsNotStale()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid permissionId = Guid.NewGuid();
        db.Permissions.Add(Permission("price.publish", permissionId));
        PolicyEntity draft = Policy("wip-policy", permissionId, "ALLOW", "{\"conditions\":[]}", "DRAFT");
        draft.CreatedAt = Now.AddDays(-1);
        db.Policies.Add(draft);
        await db.SaveChangesAsync();

        IReadOnlyList<ConfigFinding> findings = await Run(db);

        Assert.DoesNotContain(findings, finding => finding.Kind == "STALE_DRAFT");
    }

    [Fact]
    public async Task ContradictoryConditions_AreFlaggedUnfireable()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid permissionId = Guid.NewGuid();
        db.Permissions.Add(Permission("price.publish", permissionId));
        db.Policies.Add(Policy(
            "impossible",
            permissionId,
            "ALLOW",
            "{\"conditions\":[{\"attribute\":\"context.status\",\"operator\":\"eq\",\"value\":\"A\"},{\"attribute\":\"context.status\",\"operator\":\"eq\",\"value\":\"B\"}]}",
            "PUBLISHED"));
        await db.SaveChangesAsync();

        IReadOnlyList<ConfigFinding> findings = await Run(db);

        Assert.Contains(findings, finding => finding.Kind == "UNFIREABLE_RULE" && finding.EntityKey == "impossible");
    }

    [Fact]
    public async Task ImpossibleNumericRange_IsFlaggedUnfireable()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid permissionId = Guid.NewGuid();
        db.Permissions.Add(Permission("price.publish", permissionId));
        db.Policies.Add(Policy(
            "bad-range",
            permissionId,
            "DENY",
            "{\"conditions\":[{\"attribute\":\"context.amount\",\"operator\":\"gt\",\"value\":\"100\"},{\"attribute\":\"context.amount\",\"operator\":\"lt\",\"value\":\"50\"}]}",
            "PUBLISHED"));
        await db.SaveChangesAsync();

        IReadOnlyList<ConfigFinding> findings = await Run(db);

        Assert.Contains(findings, finding => finding.Kind == "UNFIREABLE_RULE" && finding.EntityKey == "bad-range");
    }

    [Fact]
    public async Task SatisfiableConditions_AreNotUnfireable()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid permissionId = Guid.NewGuid();
        db.Permissions.Add(Permission("price.publish", permissionId));
        db.Policies.Add(Policy(
            "fine",
            permissionId,
            "ALLOW",
            "{\"conditions\":[{\"attribute\":\"context.status\",\"operator\":\"eq\",\"value\":\"READY\"}]}",
            "PUBLISHED"));
        db.Decisions.Add(Decision("{\"status\":\"READY\"}"));
        await db.SaveChangesAsync();

        IReadOnlyList<ConfigFinding> findings = await Run(db);

        Assert.DoesNotContain(findings, finding => finding.Kind == "UNFIREABLE_RULE");
    }

    [Fact]
    public async Task RolesWithIdenticalPermissions_AreFlaggedDuplicate()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid roleA = Guid.NewGuid();
        Guid roleB = Guid.NewGuid();
        Guid permission1 = Guid.NewGuid();
        Guid permission2 = Guid.NewGuid();
        db.Roles.Add(Role(roleA, "role-a", privileged: false));
        db.Roles.Add(Role(roleB, "role-b", privileged: false));
        db.Permissions.Add(Permission("price.view", permission1));
        db.Permissions.Add(Permission("price.edit", permission2));
        db.RolePermissions.Add(RolePermission(roleA, permission1, "PUBLISHED"));
        db.RolePermissions.Add(RolePermission(roleA, permission2, "PUBLISHED"));
        db.RolePermissions.Add(RolePermission(roleB, permission1, "PUBLISHED"));
        db.RolePermissions.Add(RolePermission(roleB, permission2, "PUBLISHED"));
        db.Assignments.Add(ActiveAssignment(roleA));
        db.Assignments.Add(ActiveAssignment(roleB));
        await db.SaveChangesAsync();

        IReadOnlyList<ConfigFinding> findings = await Run(db);

        Assert.Contains(findings, finding => finding.Kind == "DUPLICATE_ROLES");
    }

    [Fact]
    public async Task RolesWithDistinctPermissions_AreNotDuplicate()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid roleA = Guid.NewGuid();
        Guid roleB = Guid.NewGuid();
        Guid permission1 = Guid.NewGuid();
        Guid permission2 = Guid.NewGuid();
        db.Roles.Add(Role(roleA, "role-a", privileged: false));
        db.Roles.Add(Role(roleB, "role-b", privileged: false));
        db.Permissions.Add(Permission("price.view", permission1));
        db.Permissions.Add(Permission("price.edit", permission2));
        db.RolePermissions.Add(RolePermission(roleA, permission1, "PUBLISHED"));
        db.RolePermissions.Add(RolePermission(roleB, permission2, "PUBLISHED"));
        db.Assignments.Add(ActiveAssignment(roleA));
        db.Assignments.Add(ActiveAssignment(roleB));
        await db.SaveChangesAsync();

        IReadOnlyList<ConfigFinding> findings = await Run(db);

        Assert.DoesNotContain(findings, finding => finding.Kind == "DUPLICATE_ROLES");
    }

    [Fact]
    public async Task HealthyConfiguration_ProducesNoFindings()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid roleId = Guid.NewGuid();
        Guid permissionId = Guid.NewGuid();
        db.Roles.Add(Role(roleId, "viewer", privileged: false));
        db.Permissions.Add(Permission("price.view", permissionId));
        db.RolePermissions.Add(RolePermission(roleId, permissionId, "PUBLISHED"));
        db.Assignments.Add(ActiveAssignment(roleId));
        await db.SaveChangesAsync();

        IReadOnlyList<ConfigFinding> findings = await Run(db);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task Findings_AreOrderedBySeverityHighFirst()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid privilegedRole = Guid.NewGuid();
        Guid unusedRole = Guid.NewGuid();
        Guid permissionId = Guid.NewGuid();
        // A HIGH privileged-unguarded finding and a LOW unused-role finding.
        db.Roles.Add(Role(privilegedRole, "lead", privileged: true));
        db.Roles.Add(Role(unusedRole, "spare", privileged: false));
        db.Permissions.Add(Permission("price.publish", permissionId));
        db.RolePermissions.Add(RolePermission(privilegedRole, permissionId, "PUBLISHED"));
        db.Assignments.Add(ActiveAssignment(privilegedRole));
        await db.SaveChangesAsync();

        IReadOnlyList<ConfigFinding> findings = await Run(db);

        Assert.NotEmpty(findings);
        Assert.Equal("HIGH", findings[0].Severity);
    }

    private Task<IReadOnlyList<ConfigFinding>> Run(AuthorizationDbContext db) =>
        new ConfigAdvisorBuilder(db, new FixedTimeProvider(Now)).BuildAsync(appId, ApplicationId, CancellationToken.None);

    private AuthorizationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AuthorizationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AuthorizationDbContext(options);
        db.Applications.Add(new ApplicationEntity
        {
            Id = appId,
            ApplicationId = ApplicationId,
            Name = "App Under Test",
            TenantRefId = Guid.NewGuid(),
            Status = "ACTIVE",
        });
        db.SaveChanges();
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

    private PermissionEntity Permission(string key, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        ApplicationRefId = appId,
        PermissionKey = key,
        Resource = key.Split('.')[0],
        Action = key.Contains('.') ? key.Split('.')[1] : key,
        Status = "ACTIVE",
    };

    private RolePermissionEntity RolePermission(Guid roleId, Guid permissionId, string state) => new()
    {
        Id = Guid.NewGuid(),
        ApplicationRefId = appId,
        RoleRefId = roleId,
        PermissionRefId = permissionId,
        State = state,
    };

    private AssignmentEntity ActiveAssignment(Guid roleId) => new()
    {
        Id = Guid.NewGuid(),
        ApplicationRefId = appId,
        SubjectType = "USER",
        SubjectEmail = $"user-{Guid.NewGuid():N}@icis.com",
        RoleRefId = roleId,
        ValidFrom = Now.AddDays(-10),
        State = "ACTIVE",
    };

    private PolicyEntity Policy(string key, Guid permissionId, string effect, string conditions, string state) => new()
    {
        Id = Guid.NewGuid(),
        ApplicationRefId = appId,
        PolicyKey = key,
        PermissionRefId = permissionId,
        Effect = effect,
        Conditions = conditions,
        State = state,
        CreatedAt = Now,
    };

    private DecisionEntity Decision(string contextSnapshot) => new()
    {
        DecisionId = Guid.NewGuid().ToString(),
        ApplicationId = ApplicationId,
        SubjectType = "USER",
        SubjectEmail = "user@icis.com",
        ResourceType = "price",
        Action = "publish",
        Allowed = true,
        ContextSnapshot = contextSnapshot,
        Timestamp = Now.AddMinutes(-5),
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset now = now;

        public override DateTimeOffset GetUtcNow() => now;
    }
}
