using System.Text.Json;
using System.Threading.Channels;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authorization.Infrastructure.RuntimeAuthorization;

public sealed class InProcessDecisionOutbox : IDecisionRecorder
{
    internal const int Capacity = 10_000;

    private readonly Channel<QueuedDecisionRecord> channel = Channel.CreateBounded<QueuedDecisionRecord>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true,
        SingleWriter = false,
        // The queue is intentionally lossy under sustained backpressure so that runtime
        // authorization latency is never coupled to decision persistence throughput. Drops
        // are surfaced via warning logs (with a running dropped count) instead of silently.
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    private readonly ILogger<InProcessDecisionOutbox> logger;
    private long droppedCount;

    public InProcessDecisionOutbox(ILogger<InProcessDecisionOutbox> logger)
    {
        this.logger = logger;
    }

    public ValueTask RecordAsync(DecisionRecord record, CancellationToken cancellationToken = default)
    {
        if (!channel.Writer.TryWrite(new QueuedDecisionRecord(record, Attempt: 0)))
        {
            long dropped = Interlocked.Increment(ref droppedCount);
            logger.LogWarning(
                "Decision persistence queue is full (capacity {Capacity}); dropped decision {DecisionId} for application {ApplicationId}. Total dropped so far: {DroppedCount}.",
                Capacity,
                record.DecisionId,
                record.ApplicationId,
                dropped);
        }

        return ValueTask.CompletedTask;
    }

    internal IAsyncEnumerable<QueuedDecisionRecord> ReadAllAsync(CancellationToken cancellationToken)
    {
        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    internal bool TryRequeue(QueuedDecisionRecord record)
    {
        QueuedDecisionRecord requeued = record with { Attempt = record.Attempt + 1 };
        if (channel.Writer.TryWrite(requeued))
        {
            return true;
        }

        long dropped = Interlocked.Increment(ref droppedCount);
        logger.LogWarning(
            "Decision persistence queue is full (capacity {Capacity}); dropped retry (attempt {Attempt}) for decision {DecisionId}. Total dropped so far: {DroppedCount}.",
            Capacity,
            requeued.Attempt,
            requeued.Record.DecisionId,
            dropped);
        return false;
    }
}

internal sealed record QueuedDecisionRecord(DecisionRecord Record, int Attempt);

public sealed class DecisionPersistenceWorker : BackgroundService
{
    private const int MaxAttempts = 3;
    private readonly InProcessDecisionOutbox outbox;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<DecisionPersistenceWorker> logger;

    public DecisionPersistenceWorker(
        InProcessDecisionOutbox outbox,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<DecisionPersistenceWorker> logger)
    {
        this.outbox = outbox;
        this.scopeFactory = scopeFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (QueuedDecisionRecord queuedRecord in outbox.ReadAllAsync(stoppingToken))
        {
            await PersistWithRetryAsync(queuedRecord, stoppingToken);
        }
    }

    private async Task PersistWithRetryAsync(QueuedDecisionRecord queuedRecord, CancellationToken stoppingToken)
    {
        try
        {
            await PersistAsync(queuedRecord.Record, stoppingToken);
        }
        catch (Exception exception) when (exception is DbUpdateException or InvalidOperationException)
        {
            if (queuedRecord.Attempt + 1 >= MaxAttempts)
            {
                logger.LogError(exception, "Decision persistence failed after retry budget for decision {DecisionId}.", queuedRecord.Record.DecisionId);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100 * (queuedRecord.Attempt + 1)), timeProvider, stoppingToken);
            outbox.TryRequeue(queuedRecord);
        }
    }

    private async Task PersistAsync(DecisionRecord record, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AuthorizationDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        dbContext.Decisions.Add(new DecisionEntity
        {
            DecisionId = record.DecisionId,
            ApplicationId = record.ApplicationId,
            SubjectType = record.SubjectType,
            SubjectEmail = record.SubjectEmail,
            ResourceType = record.ResourceType,
            ResourceId = record.ResourceId,
            Action = record.Action,
            ContextSnapshot = JsonSerializer.Serialize(record.Context),
            Allowed = record.Allowed,
            DenyReason = record.DenyReason,
            MatchedRoles = record.MatchedRoles.ToArray(),
            MatchedPermissions = record.MatchedPermissions.ToArray(),
            MatchedPolicies = record.MatchedPolicies.ToArray(),
            Obligations = JsonSerializer.Serialize(record.Obligations.Select(obligation => new { obligation.Id, obligation.Value })),
            VersionsUsed = "{}",
            Timestamp = record.Timestamp,
            CorrelationId = record.CorrelationId,
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
