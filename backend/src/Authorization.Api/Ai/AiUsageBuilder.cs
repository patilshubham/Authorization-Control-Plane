using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Ai;

/// <summary>
/// Aggregates the metadata-only <c>ai_invocations</c> log into a platform-admin usage report:
/// volume, outcome mix, per-feature / per-actor / per-application breakdowns, latency percentiles,
/// token totals and an estimated cost. Rows are materialised (they are low-volume advisory calls)
/// and aggregated in memory, avoiding relational <c>GROUP BY</c> translation pitfalls.
/// </summary>
public sealed class AiUsageBuilder
{
    private readonly AuthorizationDbContext dbContext;

    public AiUsageBuilder(AuthorizationDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    // Approximate USD price per 1M tokens (input, output), keyed by a case-insensitive model-name
    // substring. Unknown models yield a null cost rather than a misleading number.
    private static readonly (string Match, double InputPerMillion, double OutputPerMillion)[] PriceTable =
    [
        ("gpt-4o-mini", 0.15, 0.60),
        ("gpt-4o", 2.50, 10.00),
        ("gpt-4.1-mini", 0.40, 1.60),
        ("gpt-4.1", 2.00, 8.00),
        ("o4-mini", 1.10, 4.40),
    ];

    public async Task<AiUsageReport> BuildAsync(
        IReadOnlyCollection<string> applicationIds,
        bool includePlatformScoped,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        var allowed = new HashSet<string>(applicationIds, StringComparer.OrdinalIgnoreCase);

        List<InvocationRow> rows = await dbContext.AiInvocations
            .AsNoTracking()
            .Where(row => row.Timestamp >= fromUtc && row.Timestamp < toUtc)
            .Select(row => new InvocationRow(
                row.Feature,
                row.ActorEmail,
                row.ApplicationId,
                row.Model,
                row.Outcome,
                row.LatencyMs,
                row.PromptTokens,
                row.CompletionTokens,
                row.TotalTokens,
                row.Timestamp))
            .ToListAsync(cancellationToken);

        // Scope: an application-scoped row must be for an application the caller may audit; a
        // platform-scoped (null application) row is only visible to full platform administrators.
        List<InvocationRow> scoped = rows
            .Where(row => row.ApplicationId is null
                ? includePlatformScoped
                : allowed.Contains(row.ApplicationId))
            .ToList();

        int total = scoped.Count;
        int success = scoped.Count(r => r.Outcome == AiInvocationOutcomes.Success);
        int timeout = scoped.Count(r => r.Outcome == AiInvocationOutcomes.Timeout);
        int error = scoped.Count(r => r.Outcome == AiInvocationOutcomes.Error);

        long promptTokens = scoped.Sum(r => (long)(r.PromptTokens ?? 0));
        long completionTokens = scoped.Sum(r => (long)(r.CompletionTokens ?? 0));
        long totalTokens = scoped.Sum(r => (long)(r.TotalTokens ?? 0));

        int[] latencies = scoped.Select(r => r.LatencyMs).OrderBy(x => x).ToArray();

        List<AiUsageFeatureCount> byFeature = scoped
            .GroupBy(r => r.Feature, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AiUsageFeatureCount(
                g.Key,
                g.Count(),
                g.Count(r => r.Outcome == AiInvocationOutcomes.Success),
                g.Sum(r => (long)(r.TotalTokens ?? 0))))
            .OrderByDescending(f => f.Count)
            .ToList();

        List<AiUsageActorCount> byActor = scoped
            .Where(r => !string.IsNullOrWhiteSpace(r.ActorEmail))
            .GroupBy(r => r.ActorEmail!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AiUsageActorCount(g.Key, g.Count()))
            .OrderByDescending(a => a.Count)
            .Take(20)
            .ToList();

        List<AiUsageApplicationCount> byApplication = scoped
            .GroupBy(r => r.ApplicationId ?? "(platform)", StringComparer.OrdinalIgnoreCase)
            .Select(g => new AiUsageApplicationCount(g.Key, g.Count()))
            .OrderByDescending(a => a.Count)
            .ToList();

        List<AiUsageDailyCount> trend = scoped
            .GroupBy(r => r.Timestamp.UtcDateTime.Date)
            .Select(g => new AiUsageDailyCount(new DateTimeOffset(g.Key, TimeSpan.Zero), g.Count()))
            .OrderBy(d => d.Date)
            .ToList();

        double? estimatedCost = EstimateCost(scoped);

        return new AiUsageReport(
            fromUtc,
            toUtc,
            total,
            success,
            timeout,
            error,
            promptTokens,
            completionTokens,
            totalTokens,
            estimatedCost,
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95),
            byFeature,
            byActor,
            byApplication,
            trend);
    }

    private static double? EstimateCost(IReadOnlyCollection<InvocationRow> rows)
    {
        double cost = 0;
        bool priced = false;
        foreach (InvocationRow row in rows)
        {
            if (row.Model is null || (row.PromptTokens is null && row.CompletionTokens is null))
            {
                continue;
            }

            (double input, double output)? price = PriceFor(row.Model);
            if (price is null)
            {
                continue;
            }

            priced = true;
            cost += ((row.PromptTokens ?? 0) / 1_000_000d) * price.Value.input;
            cost += ((row.CompletionTokens ?? 0) / 1_000_000d) * price.Value.output;
        }

        return priced ? Math.Round(cost, 4) : null;
    }

    private static (double input, double output)? PriceFor(string model)
    {
        foreach ((string match, double input, double output) in PriceTable)
        {
            if (model.Contains(match, StringComparison.OrdinalIgnoreCase))
            {
                return (input, output);
            }
        }

        return null;
    }

    private static int Percentile(int[] sortedAscending, double percentile)
    {
        if (sortedAscending.Length == 0)
        {
            return 0;
        }

        int index = (int)Math.Ceiling(percentile * sortedAscending.Length) - 1;
        index = Math.Clamp(index, 0, sortedAscending.Length - 1);
        return sortedAscending[index];
    }

    // Projection materialised from the store, carrying the timestamp only for trend bucketing.
    private sealed record InvocationRow(
        string Feature,
        string? ActorEmail,
        string? ApplicationId,
        string? Model,
        string Outcome,
        int LatencyMs,
        int? PromptTokens,
        int? CompletionTokens,
        int? TotalTokens,
        DateTimeOffset Timestamp);
}

public sealed record AiUsageReport(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int TotalInvocations,
    int SuccessCount,
    int TimeoutCount,
    int ErrorCount,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    double? EstimatedCostUsd,
    int P50LatencyMs,
    int P95LatencyMs,
    IReadOnlyList<AiUsageFeatureCount> ByFeature,
    IReadOnlyList<AiUsageActorCount> ByActor,
    IReadOnlyList<AiUsageApplicationCount> ByApplication,
    IReadOnlyList<AiUsageDailyCount> Trend);

public sealed record AiUsageFeatureCount(string Feature, int Count, int SuccessCount, long TotalTokens);

public sealed record AiUsageActorCount(string Actor, int Count);

public sealed record AiUsageApplicationCount(string ApplicationId, int Count);

public sealed record AiUsageDailyCount(DateTimeOffset Date, int Count);
