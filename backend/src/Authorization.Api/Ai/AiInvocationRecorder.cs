using System.Diagnostics;
using Authorization.Ai;
using Authorization.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authorization.Api.Ai;

/// <summary>Well-known outcome values recorded for an AI invocation.</summary>
public static class AiInvocationOutcomes
{
    public const string Success = "Success";
    public const string Timeout = "Timeout";
    public const string Error = "Error";
}

/// <summary>Per-invocation context supplied by the calling controller.</summary>
public sealed record AiInvocationContext(
    string Feature,
    string? ApplicationId,
    string? Actor,
    string? ActorRole,
    string? CorrelationId);

/// <summary>Aggregated token totals observed for a single invocation, when the provider reports usage.</summary>
public readonly record struct AiTokenTotals(int PromptTokens, int CompletionTokens, int TotalTokens);

/// <summary>
/// Singleton token-usage sink shared with the (singleton) chat transport. Usage flows to the
/// request that is currently executing via an <see cref="AsyncLocal{T}"/> scope, so a single
/// instance can attribute usage to concurrent requests without any per-request wiring.
/// </summary>
public sealed class AiUsageAccumulator : IAiUsageObserver
{
    private static readonly AsyncLocal<Box?> CurrentScope = new();

    public void Record(AiTokenUsage usage)
    {
        Box? box = CurrentScope.Value;
        if (box is null)
        {
            return;
        }

        box.Prompt += usage.PromptTokens;
        box.Completion += usage.CompletionTokens;
        box.Total += usage.TotalTokens;
        box.Calls++;
    }

    /// <summary>Begins a usage-collection scope for the current async flow. Dispose to end it.</summary>
    public Scope BeginScope() => new();

    public sealed class Scope : IDisposable
    {
        private readonly Box box = new();

        internal Scope()
        {
            CurrentScope.Value = box;
        }

        /// <summary>Totals observed within the scope, or <c>null</c> when no usage was reported.</summary>
        public AiTokenTotals? Totals =>
            box.Calls > 0 ? new AiTokenTotals(box.Prompt, box.Completion, box.Total) : null;

        public void Dispose() => CurrentScope.Value = null;
    }

    private sealed class Box
    {
        public int Prompt;
        public int Completion;
        public int Total;
        public int Calls;
    }
}

/// <summary>
/// Wraps a single AI model call to enforce the per-request timeout and to persist one
/// metadata-only <see cref="AiInvocationEntity"/> row (feature, outcome, latency, token usage) so
/// platform admins can observe how the advisory AI surface is used. Recording is best-effort and
/// never changes the advisory result — a logging failure is swallowed. No prompt or response
/// content is stored.
/// </summary>
public sealed class AiInvocationRecorder
{
    private readonly AuthorizationDbContext dbContext;
    private readonly AiAvailability availability;
    private readonly AiOptions options;
    private readonly AiUsageAccumulator usageAccumulator;
    private readonly ILogger<AiInvocationRecorder> logger;

    public AiInvocationRecorder(
        AuthorizationDbContext dbContext,
        AiAvailability availability,
        IOptions<AiOptions> options,
        AiUsageAccumulator usageAccumulator,
        ILogger<AiInvocationRecorder> logger)
    {
        this.dbContext = dbContext;
        this.availability = availability;
        this.options = options.Value;
        this.usageAccumulator = usageAccumulator;
        this.logger = logger;
    }

    public async Task<T> InvokeAsync<T>(
        AiInvocationContext context,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using AiUsageAccumulator.Scope usageScope = usageAccumulator.BeginScope();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.Limits.RequestTimeoutSeconds));

        string outcome = AiInvocationOutcomes.Error;
        try
        {
            T result = await operation(timeoutCts.Token);
            outcome = AiInvocationOutcomes.Success;
            return result;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            outcome = AiInvocationOutcomes.Timeout;
            throw;
        }
        catch
        {
            outcome = AiInvocationOutcomes.Error;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            await RecordAsync(context, outcome, (int)stopwatch.ElapsedMilliseconds, usageScope.Totals);
        }
    }

    private async Task RecordAsync(AiInvocationContext context, string outcome, int latencyMs, AiTokenTotals? totals)
    {
        try
        {
            bool live = !string.Equals(availability.Provider, AiProviders.Fake, StringComparison.OrdinalIgnoreCase);
            dbContext.AiInvocations.Add(new AiInvocationEntity
            {
                Feature = context.Feature,
                ActorEmail = context.Actor,
                ActorRole = context.ActorRole,
                ApplicationId = context.ApplicationId,
                Provider = availability.Provider,
                Model = live ? options.AzureOpenAI.ChatDeployment : null,
                Outcome = outcome,
                LatencyMs = latencyMs,
                PromptTokens = totals?.PromptTokens,
                CompletionTokens = totals?.CompletionTokens,
                TotalTokens = totals?.TotalTokens,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = context.CorrelationId,
            });

            // CancellationToken.None so the observability row still persists even when the request
            // was aborted or the AI call timed out.
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record AI invocation for feature {Feature}.", context.Feature);
        }
    }
}
