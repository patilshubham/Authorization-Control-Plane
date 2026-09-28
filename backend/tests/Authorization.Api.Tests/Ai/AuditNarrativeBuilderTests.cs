using Authorization.Api.Ai;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Tests.Ai;

/// <summary>
/// Unit tests for <see cref="AuditNarrativeBuilder"/>. The change feed and its grouped counts are
/// fully deterministic and read-only. These tests seed a compact set of audit events across two
/// applications and assert scoping (only auditable apps), window filtering, the optional
/// application/actor/type filters, group counts, and the deny-reason aggregate.
/// </summary>
public sealed class AuditNarrativeBuilderTests
{
    private const string InScopeApp = "pricing-management";
    private const string OtherApp = "trading-desk";

    [Fact]
    public async Task ReturnsEvents_ScopedToAuditableApps_NewestFirst()
    {
        await using AuthorizationDbContext db = CreateContext();
        AddEvent(db, InScopeApp, "RoleCreated", "admin@local.test", occurredDaysAgo: 2);
        AddEvent(db, InScopeApp, "RoleUpdated", "admin@local.test", occurredDaysAgo: 1);
        // An event in an application the caller may NOT audit must never appear.
        AddEvent(db, OtherApp, "RoleDeleted", "mallory@local.test", occurredDaysAgo: 1);
        await db.SaveChangesAsync();

        AuditNarrative narrative = await BuildAsync(db, [InScopeApp]);

        Assert.Equal(2, narrative.Events.Count);
        Assert.All(narrative.Events, e => Assert.Equal(InScopeApp, e.ApplicationId));
        // Newest first.
        Assert.Equal("RoleUpdated", narrative.Events[0].EventType);
        Assert.Equal("RoleCreated", narrative.Events[1].EventType);
    }

    [Fact]
    public async Task ExcludesEventsOutsideWindow()
    {
        await using AuthorizationDbContext db = CreateContext();
        AddEvent(db, InScopeApp, "RoleCreated", "admin@local.test", occurredDaysAgo: 1);
        AddEvent(db, InScopeApp, "RoleArchived", "admin@local.test", occurredDaysAgo: 90);
        await db.SaveChangesAsync();

        AuditNarrative narrative = await BuildAsync(db, [InScopeApp], windowDays: 30);

        AuditNarrativeEvent item = Assert.Single(narrative.Events);
        Assert.Equal("RoleCreated", item.EventType);
    }

    [Fact]
    public async Task AppliesActorAndTypeFilters()
    {
        await using AuthorizationDbContext db = CreateContext();
        AddEvent(db, InScopeApp, "RoleCreated", "admin@local.test", occurredDaysAgo: 1);
        AddEvent(db, InScopeApp, "RoleCreated", "other@local.test", occurredDaysAgo: 1);
        AddEvent(db, InScopeApp, "PolicyPublished", "admin@local.test", occurredDaysAgo: 1);
        await db.SaveChangesAsync();

        AuditNarrative byActor = await BuildAsync(db, [InScopeApp], actorEmail: "admin@local.test");
        Assert.Equal(2, byActor.Events.Count);
        Assert.All(byActor.Events, e => Assert.Equal("admin@local.test", e.ActorEmail));

        AuditNarrative byType = await BuildAsync(db, [InScopeApp], eventType: "RoleCreated");
        Assert.Equal(2, byType.Events.Count);
        Assert.All(byType.Events, e => Assert.Equal("RoleCreated", e.EventType));
    }

    [Fact]
    public async Task ComputesGroupCounts()
    {
        await using AuthorizationDbContext db = CreateContext();
        AddEvent(db, InScopeApp, "RoleCreated", "admin@local.test", occurredDaysAgo: 1);
        AddEvent(db, InScopeApp, "RoleCreated", "admin@local.test", occurredDaysAgo: 1);
        AddEvent(db, InScopeApp, "PolicyPublished", "other@local.test", occurredDaysAgo: 1);
        await db.SaveChangesAsync();

        AuditNarrative narrative = await BuildAsync(db, [InScopeApp]);

        AuditGroupCount app = Assert.Single(narrative.ByApplication);
        Assert.Equal(InScopeApp, app.Key);
        Assert.Equal(3, app.Count);

        // Most frequent type first.
        Assert.Equal("RoleCreated", narrative.ByEventType[0].Key);
        Assert.Equal(2, narrative.ByEventType[0].Count);

        // Most active actor first.
        Assert.Equal("admin@local.test", narrative.ByActor[0].Key);
        Assert.Equal(2, narrative.ByActor[0].Count);
    }

    [Fact]
    public async Task IncludesTopDenyReasons_ScopedToWindowAndApps()
    {
        await using AuthorizationDbContext db = CreateContext();
        AddEvent(db, InScopeApp, "RoleCreated", "admin@local.test", occurredDaysAgo: 1);
        AddDenial(db, InScopeApp, "no matching role", occurredDaysAgo: 1);
        AddDenial(db, InScopeApp, "no matching role", occurredDaysAgo: 2);
        // Out-of-scope denial must be excluded.
        AddDenial(db, OtherApp, "blocked by policy", occurredDaysAgo: 1);
        await db.SaveChangesAsync();

        AuditNarrative narrative = await BuildAsync(db, [InScopeApp]);

        AuditDenyReasonCount deny = Assert.Single(narrative.TopDenyReasons);
        Assert.Equal(InScopeApp, deny.ApplicationId);
        Assert.Equal("no matching role", deny.DenyReason);
        Assert.Equal(2, deny.Count);
    }

    [Fact]
    public async Task EmptyScope_ReturnsEmptyNarrative()
    {
        await using AuthorizationDbContext db = CreateContext();
        AddEvent(db, InScopeApp, "RoleCreated", "admin@local.test", occurredDaysAgo: 1);
        await db.SaveChangesAsync();

        AuditNarrative narrative = await BuildAsync(db, []);

        Assert.Empty(narrative.Events);
        Assert.Empty(narrative.ByApplication);
        Assert.Empty(narrative.TopDenyReasons);
    }

    private static Task<AuditNarrative> BuildAsync(
        AuthorizationDbContext db,
        IReadOnlyList<string> auditableApps,
        int windowDays = 30,
        string? applicationId = null,
        string? actorEmail = null,
        string? eventType = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var query = new AuditNarrativeQuery(now.AddDays(-windowDays), now, applicationId, actorEmail, eventType);
        return new AuditNarrativeBuilder(db).BuildAsync(auditableApps, query, CancellationToken.None);
    }

    private static AuthorizationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AuthorizationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthorizationDbContext(options);
    }

    private static void AddEvent(AuthorizationDbContext db, string applicationId, string eventType, string actorEmail, int occurredDaysAgo)
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            ApplicationId = applicationId,
            ActorEmail = actorEmail,
            Timestamp = DateTimeOffset.UtcNow.AddDays(-occurredDaysAgo),
            Reason = "seed",
        });
    }

    private static void AddDenial(AuthorizationDbContext db, string applicationId, string denyReason, int occurredDaysAgo)
    {
        db.Decisions.Add(new DecisionEntity
        {
            DecisionId = Guid.NewGuid().ToString("N"),
            ApplicationId = applicationId,
            SubjectType = "USER",
            SubjectEmail = "user@local.test",
            ResourceType = "generic",
            Action = "price.view",
            ContextSnapshot = "{}",
            Allowed = false,
            DenyReason = denyReason,
            MatchedRoles = [],
            MatchedPermissions = [],
            MatchedPolicies = [],
            VersionsUsed = "{}",
            Timestamp = DateTimeOffset.UtcNow.AddDays(-occurredDaysAgo),
        });
    }
}
