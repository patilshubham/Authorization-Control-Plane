using Authorization.Api.Ai;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Tests.Ai;

/// <summary>
/// Unit tests for <see cref="AiUsageBuilder"/>. The invocation log is metadata-only and the report
/// is fully deterministic. These tests seed a compact set of invocations and assert application
/// scoping (auditable apps plus optional platform-scoped rows), window filtering, outcome and token
/// aggregates, per-feature / per-actor breakdowns, latency percentiles and the estimated cost.
/// </summary>
public sealed class AiUsageBuilderTests
{
    private const string InScopeApp = "pricing-management";
    private const string OtherApp = "trading-desk";

    [Fact]
    public async Task AggregatesTotalsOutcomesAndTokens()
    {
        await using AuthorizationDbContext db = CreateContext();
        AddInvocation(db, "policyAuthoring", "admin@local.test", InScopeApp, AiInvocationOutcomes.Success, latencyMs: 100, prompt: 10, completion: 20);
        AddInvocation(db, "policyAuthoring", "admin@local.test", InScopeApp, AiInvocationOutcomes.Timeout, latencyMs: 200);
        AddInvocation(db, "decisionExplainer", "other@local.test", InScopeApp, AiInvocationOutcomes.Error, latencyMs: 300);
        await db.SaveChangesAsync();

        AiUsageReport report = await BuildAsync(db, [InScopeApp]);

        Assert.Equal(3, report.TotalInvocations);
        Assert.Equal(1, report.SuccessCount);
        Assert.Equal(1, report.TimeoutCount);
        Assert.Equal(1, report.ErrorCount);
        Assert.Equal(10, report.PromptTokens);
        Assert.Equal(20, report.CompletionTokens);
        Assert.Equal(30, report.TotalTokens);
    }

    [Fact]
    public async Task ScopesApplicationRowsToAuditableApps()
    {
        await using AuthorizationDbContext db = CreateContext();
        AddInvocation(db, "policyAuthoring", "admin@local.test", InScopeApp, AiInvocationOutcomes.Success, latencyMs: 100);
        // An invocation against an application the caller may not audit must never appear.
        AddInvocation(db, "policyAuthoring", "mallory@local.test", OtherApp, AiInvocationOutcomes.Success, latencyMs: 100);
        await db.SaveChangesAsync();

        AiUsageReport report = await BuildAsync(db, [InScopeApp]);

        Assert.Equal(1, report.TotalInvocations);
        AiUsageApplicationCount app = Assert.Single(report.ByApplication);
        Assert.Equal(InScopeApp, app.ApplicationId);
    }

    [Fact]
    public async Task PlatformScopedRows_OnlyVisibleWhenIncluded()
    {
        await using AuthorizationDbContext db = CreateContext();
        AddInvocation(db, "accessSearch", "admin@local.test", applicationId: null, AiInvocationOutcomes.Success, latencyMs: 100);
        AddInvocation(db, "policyAuthoring", "admin@local.test", InScopeApp, AiInvocationOutcomes.Success, latencyMs: 100);
        await db.SaveChangesAsync();

        AiUsageReport delegated = await BuildAsync(db, [InScopeApp], includePlatformScoped: false);
        Assert.Equal(1, delegated.TotalInvocations);

        AiUsageReport platform = await BuildAsync(db, [InScopeApp], includePlatformScoped: true);
        Assert.Equal(2, platform.TotalInvocations);
        Assert.Contains(platform.ByApplication, a => a.ApplicationId == "(platform)");
    }

    [Fact]
    public async Task ExcludesInvocationsOutsideWindow()
    {
        await using AuthorizationDbContext db = CreateContext();
        AddInvocation(db, "policyAuthoring", "admin@local.test", InScopeApp, AiInvocationOutcomes.Success, latencyMs: 100, daysAgo: 1);
        AddInvocation(db, "policyAuthoring", "admin@local.test", InScopeApp, AiInvocationOutcomes.Success, latencyMs: 100, daysAgo: 60);
        await db.SaveChangesAsync();

        AiUsageReport report = await BuildAsync(db, [InScopeApp], windowDays: 30);

        Assert.Equal(1, report.TotalInvocations);
    }

    [Fact]
    public async Task RanksFeaturesAndActorsByVolume()
    {
        await using AuthorizationDbContext db = CreateContext();
        AddInvocation(db, "policyAuthoring", "admin@local.test", InScopeApp, AiInvocationOutcomes.Success, latencyMs: 100);
        AddInvocation(db, "policyAuthoring", "admin@local.test", InScopeApp, AiInvocationOutcomes.Success, latencyMs: 100);
        AddInvocation(db, "decisionExplainer", "other@local.test", InScopeApp, AiInvocationOutcomes.Success, latencyMs: 100);
        await db.SaveChangesAsync();

        AiUsageReport report = await BuildAsync(db, [InScopeApp]);

        Assert.Equal("policyAuthoring", report.ByFeature[0].Feature);
        Assert.Equal(2, report.ByFeature[0].Count);
        Assert.Equal("admin@local.test", report.ByActor[0].Actor);
        Assert.Equal(2, report.ByActor[0].Count);
    }

    [Fact]
    public async Task ComputesLatencyPercentiles()
    {
        await using AuthorizationDbContext db = CreateContext();
        foreach (int latency in new[] { 10, 20, 30, 40, 100 })
        {
            AddInvocation(db, "policyAuthoring", "admin@local.test", InScopeApp, AiInvocationOutcomes.Success, latencyMs: latency);
        }
        await db.SaveChangesAsync();

        AiUsageReport report = await BuildAsync(db, [InScopeApp]);

        Assert.Equal(30, report.P50LatencyMs);
        Assert.Equal(100, report.P95LatencyMs);
    }

    [Fact]
    public async Task EstimatesCost_ForKnownModel_NullForUnknown()
    {
        await using AuthorizationDbContext db = CreateContext();
        AddInvocation(db, "policyAuthoring", "admin@local.test", InScopeApp, AiInvocationOutcomes.Success, latencyMs: 100, prompt: 1_000_000, completion: 1_000_000, model: "openai/gpt-4o-mini");
        await db.SaveChangesAsync();

        AiUsageReport known = await BuildAsync(db, [InScopeApp]);
        // 1M input @ $0.15 + 1M output @ $0.60 = $0.75.
        Assert.NotNull(known.EstimatedCostUsd);
        Assert.Equal(0.75, known.EstimatedCostUsd!.Value, 3);

        await using AuthorizationDbContext unknownDb = CreateContext();
        AddInvocation(unknownDb, "policyAuthoring", "admin@local.test", InScopeApp, AiInvocationOutcomes.Success, latencyMs: 100, prompt: 100, completion: 100, model: "mystery-model-9000");
        await unknownDb.SaveChangesAsync();

        AiUsageReport unknown = await BuildAsync(unknownDb, [InScopeApp]);
        Assert.Null(unknown.EstimatedCostUsd);
    }

    [Fact]
    public async Task EmptyLog_ReturnsZeroedReport()
    {
        await using AuthorizationDbContext db = CreateContext();

        AiUsageReport report = await BuildAsync(db, [InScopeApp]);

        Assert.Equal(0, report.TotalInvocations);
        Assert.Equal(0, report.TotalTokens);
        Assert.Equal(0, report.P50LatencyMs);
        Assert.Empty(report.ByFeature);
        Assert.Empty(report.ByActor);
        Assert.Empty(report.Trend);
        Assert.Null(report.EstimatedCostUsd);
    }

    private static Task<AiUsageReport> BuildAsync(
        AuthorizationDbContext db,
        IReadOnlyCollection<string> auditableApps,
        bool includePlatformScoped = true,
        int windowDays = 30)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new AiUsageBuilder(db).BuildAsync(
            auditableApps,
            includePlatformScoped,
            now.AddDays(-windowDays),
            now.AddSeconds(1),
            CancellationToken.None);
    }

    private static AuthorizationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AuthorizationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthorizationDbContext(options);
    }

    private static void AddInvocation(
        AuthorizationDbContext db,
        string feature,
        string actorEmail,
        string? applicationId,
        string outcome,
        int latencyMs,
        int? prompt = null,
        int? completion = null,
        int daysAgo = 1,
        string model = "openai/gpt-4o-mini")
    {
        int? total = prompt is null && completion is null ? null : (prompt ?? 0) + (completion ?? 0);
        db.AiInvocations.Add(new AiInvocationEntity
        {
            Id = Guid.NewGuid(),
            Feature = feature,
            ActorEmail = actorEmail,
            ActorRole = "PlatformSuperAdmin",
            ApplicationId = applicationId,
            Provider = "OpenAI",
            Model = model,
            Outcome = outcome,
            LatencyMs = latencyMs,
            PromptTokens = prompt,
            CompletionTokens = completion,
            TotalTokens = total,
            Timestamp = DateTimeOffset.UtcNow.AddDays(-daysAgo),
            CorrelationId = "test",
        });
    }
}
