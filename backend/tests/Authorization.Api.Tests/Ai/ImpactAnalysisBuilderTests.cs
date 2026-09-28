using Authorization.Ai;
using Authorization.Api.Ai;
using Authorization.Infrastructure.Persistence;
using Authorization.Infrastructure.RuntimeAuthorization;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Tests.Ai;

/// <summary>
/// Unit tests for <see cref="ImpactAnalysisBuilder"/>. They seed an in-memory store, run the builder
/// against the real <see cref="EfAuthorizationPolicyEngine"/>, and assert the deterministic
/// allow/deny flips produced by shadow-publishing a draft policy. The in-memory provider does not
/// support transactions, so these exercise the builder's non-transactional revert path.
/// </summary>
public sealed class ImpactAnalysisBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 13, 0, 0, 0, TimeSpan.Zero);
    private const string ApplicationId = "pricing-management";

    private readonly Guid appId = Guid.NewGuid();
    private readonly Guid leadRoleId = Guid.NewGuid();
    private readonly Guid publishPermissionId = Guid.NewGuid();

    [Fact]
    public async Task Build_UnknownDraftPolicy_ReturnsNull()
    {
        await using AuthorizationDbContext db = await SeedAsync();
        ImpactAnalysisBuilder builder = CreateBuilder(db);

        ImpactAnalysisFacts? result = await builder.BuildAsync(
            appId, ApplicationId, "does-not-exist", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Build_DenyDraft_FlipsHistoricallyAllowedSubjectToDenied()
    {
        await using AuthorizationDbContext db = await SeedAsync();

        // A representative recorded request for the guarded resource/action.
        db.Decisions.Add(new DecisionEntity
        {
            DecisionId = Guid.NewGuid().ToString(),
            ApplicationId = ApplicationId,
            SubjectType = "USER",
            SubjectEmail = "lead@icis.com",
            ResourceType = "price",
            Action = "publish",
            Allowed = true,
            Timestamp = Now.AddMinutes(-5),
        });

        // A draft DENY policy on the same permission that always matches (empty conditions).
        db.Policies.Add(new PolicyEntity
        {
            Id = Guid.NewGuid(),
            ApplicationRefId = appId,
            PolicyKey = "deny-all-publish",
            PermissionRefId = publishPermissionId,
            Effect = "DENY",
            Conditions = "{\"match\":\"all\",\"conditions\":[]}",
            State = "DRAFT",
        });
        await db.SaveChangesAsync();

        ImpactAnalysisBuilder builder = CreateBuilder(db);
        ImpactAnalysisFacts? result = await builder.BuildAsync(
            appId, ApplicationId, "deny-all-publish", CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.SampledFromHistory);
        Assert.Equal(1, result.EvaluatedCount);
        Assert.Equal(1, result.AllowToDenyCount);
        Assert.Equal(0, result.DenyToAllowCount);
        ImpactFlipFact flip = Assert.Single(result.Flips);
        Assert.Equal("lead@icis.com", flip.SubjectEmail);
        Assert.True(flip.Before);
        Assert.False(flip.After);

        // The draft must remain unpublished after analysis.
        PolicyEntity draft = await db.Policies.AsNoTracking().SingleAsync(p => p.PolicyKey == "deny-all-publish");
        Assert.Equal("DRAFT", draft.State);
    }

    [Fact]
    public async Task Build_AllowDraftThatDoesNotChangeOutcome_ReportsNoFlips()
    {
        await using AuthorizationDbContext db = await SeedAsync();

        db.Decisions.Add(new DecisionEntity
        {
            DecisionId = Guid.NewGuid().ToString(),
            ApplicationId = ApplicationId,
            SubjectType = "USER",
            SubjectEmail = "lead@icis.com",
            ResourceType = "price",
            Action = "publish",
            Allowed = true,
            Timestamp = Now.AddMinutes(-5),
        });

        // An ALLOW draft with no conditions: the subject was already allowed by RBAC, so nothing flips.
        db.Policies.Add(new PolicyEntity
        {
            Id = Guid.NewGuid(),
            ApplicationRefId = appId,
            PolicyKey = "allow-all-publish",
            PermissionRefId = publishPermissionId,
            Effect = "ALLOW",
            Conditions = "{\"match\":\"all\",\"conditions\":[]}",
            State = "DRAFT",
        });
        await db.SaveChangesAsync();

        ImpactAnalysisBuilder builder = CreateBuilder(db);
        ImpactAnalysisFacts? result = await builder.BuildAsync(
            appId, ApplicationId, "allow-all-publish", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result!.EvaluatedCount);
        Assert.Equal(0, result.AllowToDenyCount);
        Assert.Equal(0, result.DenyToAllowCount);
        Assert.Empty(result.Flips);
    }

    [Fact]
    public async Task Build_NoDecisionHistory_FallsBackToActiveAssignments()
    {
        await using AuthorizationDbContext db = await SeedAsync();

        db.Policies.Add(new PolicyEntity
        {
            Id = Guid.NewGuid(),
            ApplicationRefId = appId,
            PolicyKey = "deny-all-publish",
            PermissionRefId = publishPermissionId,
            Effect = "DENY",
            Conditions = "{\"match\":\"all\",\"conditions\":[]}",
            State = "DRAFT",
        });
        await db.SaveChangesAsync();

        ImpactAnalysisBuilder builder = CreateBuilder(db);
        ImpactAnalysisFacts? result = await builder.BuildAsync(
            appId, ApplicationId, "deny-all-publish", CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.SampledFromHistory);
        Assert.Equal(1, result.EvaluatedCount);
        Assert.Equal(1, result.AllowToDenyCount);
    }

    private ImpactAnalysisBuilder CreateBuilder(AuthorizationDbContext db)
    {
        var timeProvider = new FixedTimeProvider(Now);
        var engine = new EfAuthorizationPolicyEngine(db, timeProvider);
        return new ImpactAnalysisBuilder(db, engine, timeProvider);
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
            ApplicationId = ApplicationId,
            Name = "Pricing Management",
            TenantRefId = Guid.NewGuid(),
            Status = "ACTIVE",
        });

        db.Roles.Add(new RoleEntity { Id = leadRoleId, ApplicationRefId = appId, RoleKey = "pricing-lead", Name = "Pricing Lead" });

        db.Permissions.Add(new PermissionEntity
        {
            Id = publishPermissionId,
            ApplicationRefId = appId,
            PermissionKey = "price.publish",
            Resource = "price",
            Action = "publish",
            Status = "ACTIVE",
        });

        db.RolePermissions.Add(new RolePermissionEntity
        {
            Id = Guid.NewGuid(),
            ApplicationRefId = appId,
            RoleRefId = leadRoleId,
            PermissionRefId = publishPermissionId,
            State = "PUBLISHED",
        });

        db.Assignments.Add(new AssignmentEntity
        {
            Id = Guid.NewGuid(),
            ApplicationRefId = appId,
            SubjectType = "USER",
            SubjectEmail = "lead@icis.com",
            RoleRefId = leadRoleId,
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
