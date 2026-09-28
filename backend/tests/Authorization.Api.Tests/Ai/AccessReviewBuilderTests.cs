using Authorization.Api.Ai;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Tests.Ai;

/// <summary>
/// Unit tests for <see cref="AccessReviewBuilder"/>. Every recommendation is deterministic and grounded
/// only in the gathered facts (grant age, recorded usage, privilege, peer count). These tests seed a
/// compact pricing-like model and assert the keep/revoke/review outcome for each scenario, the
/// last-used signal derived from recorded decisions, peer-count comparison, and the ordering.
/// </summary>
public sealed class AccessReviewBuilderTests
{
    private const string ApplicationId = "pricing-management";
    private readonly Guid appId = Guid.NewGuid();

    [Fact]
    public async Task DormantNonPrivilegedGrant_IsRecommendedRevoke()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid editor = AddRole(db, "pricing-editor", privileged: false);
        AddAssignment(db, editor, "user13.reviewer@icis.com", grantedDaysAgo: 120);
        await db.SaveChangesAsync();

        AccessReviewItem item = await SingleItemAsync(db);

        Assert.Equal("REVOKE", item.Recommendation);
        Assert.True(item.Dormant);
        Assert.Null(item.LastUsedDaysAgo);
    }

    [Fact]
    public async Task DormantPrivilegedGrant_IsRecommendedReview()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid admin = AddRole(db, "pricing-admin", privileged: true);
        AddAssignment(db, admin, "user13.reviewer@icis.com", grantedDaysAgo: 120);
        await db.SaveChangesAsync();

        AccessReviewItem item = await SingleItemAsync(db);

        Assert.Equal("REVIEW", item.Recommendation);
        Assert.True(item.Dormant);
        Assert.True(item.Privileged);
    }

    [Fact]
    public async Task RecentlyUsedGrant_IsRecommendedKeep_WithLastUsedFromDecisions()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid analyst = AddRole(db, "pricing-analyst", privileged: false);
        AddAssignment(db, analyst, "user13.reviewer@icis.com", grantedDaysAgo: 120);
        // Case-insensitive role match against the decision's MatchedRoles.
        AddDecision(db, "user13.reviewer@icis.com", ["PRICING-ANALYST"], usedDaysAgo: 5);
        await db.SaveChangesAsync();

        AccessReviewItem item = await SingleItemAsync(db);

        Assert.Equal("KEEP", item.Recommendation);
        Assert.False(item.Dormant);
        Assert.Equal(5, item.LastUsedDaysAgo);
    }

    [Fact]
    public async Task RecentlyGrantedUnusedGrant_IsRecommendedKeep_TooNewToJudge()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid editor = AddRole(db, "pricing-editor", privileged: false);
        AddAssignment(db, editor, "user13.reviewer@icis.com", grantedDaysAgo: 10);
        await db.SaveChangesAsync();

        AccessReviewItem item = await SingleItemAsync(db);

        Assert.Equal("KEEP", item.Recommendation);
        Assert.False(item.Dormant);
        Assert.Contains("too new", item.RecommendationReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrivilegedGrantUsedButNoPeers_IsRecommendedReview()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid admin = AddRole(db, "pricing-admin", privileged: true);
        AddAssignment(db, admin, "user13.reviewer@icis.com", grantedDaysAgo: 120);
        AddDecision(db, "user13.reviewer@icis.com", ["pricing-admin"], usedDaysAgo: 3);
        await db.SaveChangesAsync();

        AccessReviewItem item = await SingleItemAsync(db);

        Assert.Equal("REVIEW", item.Recommendation);
        Assert.False(item.Dormant);
        Assert.Equal(0, item.PeerCount);
        Assert.Contains("outlier", item.RecommendationReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PeerCount_CountsOtherActiveHoldersOfTheSameRole()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid analyst = AddRole(db, "pricing-analyst", privileged: false);
        AddAssignment(db, analyst, "user13.reviewer@icis.com", grantedDaysAgo: 120);
        AddAssignment(db, analyst, "peer1@icis.com", grantedDaysAgo: 30);
        AddAssignment(db, analyst, "peer2@icis.com", grantedDaysAgo: 30);
        await db.SaveChangesAsync();

        AccessReviewItem item = await SingleItemAsync(db);

        Assert.Equal(2, item.PeerCount);
    }

    [Fact]
    public async Task ItemsAreOrdered_ReviewThenRevokeThenKeep()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid analyst = AddRole(db, "pricing-analyst", privileged: false);
        Guid editor = AddRole(db, "pricing-editor", privileged: false);
        Guid admin = AddRole(db, "pricing-admin", privileged: true);
        AddAssignment(db, analyst, "user13.reviewer@icis.com", grantedDaysAgo: 120); // used -> KEEP
        AddAssignment(db, editor, "user13.reviewer@icis.com", grantedDaysAgo: 120);   // unused -> REVOKE
        AddAssignment(db, admin, "user13.reviewer@icis.com", grantedDaysAgo: 120);    // unused priv -> REVIEW
        AddDecision(db, "user13.reviewer@icis.com", ["pricing-analyst"], usedDaysAgo: 5);
        await db.SaveChangesAsync();

        AccessReview review = await BuildAsync(db);

        Assert.Collection(
            review.Items.Select(i => i.Recommendation),
            r => Assert.Equal("REVIEW", r),
            r => Assert.Equal("REVOKE", r),
            r => Assert.Equal("KEEP", r));
    }

    [Fact]
    public async Task SubjectWithNoGrantsInScope_ReturnsEmptyReview()
    {
        await using AuthorizationDbContext db = CreateContext();
        Guid analyst = AddRole(db, "pricing-analyst", privileged: false);
        AddAssignment(db, analyst, "someone.else@icis.com", grantedDaysAgo: 120);
        await db.SaveChangesAsync();

        AccessReview review = await BuildAsync(db);

        Assert.Empty(review.Items);
    }

    private async Task<AccessReviewItem> SingleItemAsync(AuthorizationDbContext db)
    {
        AccessReview review = await BuildAsync(db);
        return Assert.Single(review.Items);
    }

    private Task<AccessReview> BuildAsync(AuthorizationDbContext db) =>
        new AccessReviewBuilder(db, TimeProvider.System).BuildAsync(
            "user13.reviewer@icis.com",
            [new AccessReviewScope(appId, ApplicationId)],
            CancellationToken.None);

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
            Name = "Pricing Management",
            TenantRefId = Guid.NewGuid(),
            Status = "ACTIVE",
        });
        db.SaveChanges();
        return db;
    }

    private Guid AddRole(AuthorizationDbContext db, string key, bool privileged)
    {
        var id = Guid.NewGuid();
        db.Roles.Add(new RoleEntity
        {
            Id = id,
            ApplicationRefId = appId,
            RoleKey = key,
            Name = key,
            Privileged = privileged,
            RiskLevel = privileged ? "HIGH" : "MEDIUM",
            Status = "ACTIVE",
        });
        return id;
    }

    private void AddAssignment(AuthorizationDbContext db, Guid roleId, string email, int grantedDaysAgo)
    {
        db.Assignments.Add(new AssignmentEntity
        {
            Id = Guid.NewGuid(),
            ApplicationRefId = appId,
            SubjectType = "USER",
            SubjectEmail = email,
            RoleRefId = roleId,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-grantedDaysAgo),
            State = "ACTIVE",
            Source = "MANUAL",
        });
    }

    private void AddDecision(AuthorizationDbContext db, string email, string[] matchedRoles, int usedDaysAgo)
    {
        db.Decisions.Add(new DecisionEntity
        {
            DecisionId = Guid.NewGuid().ToString("N"),
            ApplicationId = ApplicationId,
            SubjectType = "USER",
            SubjectEmail = email,
            ResourceType = "generic",
            Action = "price.view",
            ContextSnapshot = "{}",
            Allowed = true,
            MatchedRoles = matchedRoles,
            MatchedPermissions = [],
            MatchedPolicies = [],
            VersionsUsed = "{}",
            Timestamp = DateTimeOffset.UtcNow.AddDays(-usedDaysAgo),
        });
    }
}
