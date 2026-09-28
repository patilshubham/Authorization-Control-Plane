using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Ai;

/// <summary>
/// Gathers read-only, deterministic facts for an audit-narrative (compliance-evidence) summary of a
/// change window: the raw audit events in range (scoped to the applications the caller may audit),
/// grouped counts by application / actor / change-type, and a small deny-reason aggregate drawn from
/// recorded decisions. Mirrors <see cref="AccessReviewBuilder"/> — every query is read-only and
/// nothing here mutates state. The AI layer only narrates and cites these facts; it never invents an
/// event, and it receives no personal data (actors and targets are pseudonymized by the caller).
/// </summary>
public sealed class AuditNarrativeBuilder
{
    // Hard cap so a very wide window cannot pull an unbounded event set into memory or the prompt.
    private const int MaxEvents = 300;

    // Only the most common deny reasons are worth surfacing as a compliance signal.
    private const int MaxDenyReasons = 10;

    private readonly AuthorizationDbContext dbContext;

    public AuditNarrativeBuilder(AuthorizationDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task<AuditNarrative> BuildAsync(
        IReadOnlyList<string> auditableApplicationIds,
        AuditNarrativeQuery query,
        CancellationToken cancellationToken)
    {
        var scopedApps = new HashSet<string>(auditableApplicationIds, StringComparer.OrdinalIgnoreCase);
        if (scopedApps.Count == 0)
        {
            return new AuditNarrative(query.FromUtc, query.ToUtc, [], [], [], [], []);
        }

        // 1 — Raw audit events in the window, scoped to the applications the caller may audit.
        IQueryable<AuditEventEntity> events = dbContext.AuditEvents
            .AsNoTracking()
            .Where(entity => entity.Timestamp >= query.FromUtc && entity.Timestamp <= query.ToUtc
                && entity.ApplicationId != null && scopedApps.Contains(entity.ApplicationId));

        if (!string.IsNullOrWhiteSpace(query.ApplicationId))
        {
            events = events.Where(entity => entity.ApplicationId == query.ApplicationId);
        }

        if (!string.IsNullOrWhiteSpace(query.ActorEmail))
        {
            events = events.Where(entity => entity.ActorEmail == query.ActorEmail);
        }

        if (!string.IsNullOrWhiteSpace(query.EventType))
        {
            events = events.Where(entity => entity.EventType == query.EventType);
        }

        List<AuditEventEntity> rows = await events
            .OrderByDescending(entity => entity.Timestamp)
            .Take(MaxEvents)
            .ToListAsync(cancellationToken);

        List<AuditNarrativeEvent> narrativeEvents = rows
            .Select(entity => new AuditNarrativeEvent(
                entity.EventId.ToString("N"),
                entity.EventType,
                entity.ApplicationId,
                entity.ActorEmail,
                entity.TargetSubjectEmail,
                entity.Timestamp,
                entity.OldValue,
                entity.NewValue,
                entity.Reason))
            .ToList();

        // 2 — Deterministic group counts over the returned events.
        List<AuditGroupCount> byApplication = narrativeEvents
            .Where(e => e.ApplicationId is not null)
            .GroupBy(e => e.ApplicationId!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AuditGroupCount(g.Key, g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<AuditGroupCount> byActor = narrativeEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.ActorEmail))
            .GroupBy(e => e.ActorEmail!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AuditGroupCount(g.Key, g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<AuditGroupCount> byEventType = narrativeEvents
            .GroupBy(e => e.EventType, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AuditGroupCount(g.Key, g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 3 — Optional deny-reason aggregate from recorded decisions in the same window/scope. This is
        // a secondary compliance signal (why access was denied), never part of the change feed itself.
        // The grouped aggregate is projected into an anonymous type so it translates on relational
        // providers, then mapped to the response record in memory.
        var denyGroups = await dbContext.Decisions
            .AsNoTracking()
            .Where(decision => decision.Timestamp >= query.FromUtc && decision.Timestamp <= query.ToUtc
                && !decision.Allowed
                && decision.DenyReason != null
                && scopedApps.Contains(decision.ApplicationId)
                && (string.IsNullOrWhiteSpace(query.ApplicationId) || decision.ApplicationId == query.ApplicationId))
            .GroupBy(decision => new { decision.ApplicationId, decision.DenyReason })
            .Select(g => new { g.Key.ApplicationId, g.Key.DenyReason, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(MaxDenyReasons)
            .ToListAsync(cancellationToken);

        List<AuditDenyReasonCount> topDenyReasons = denyGroups
            .Select(g => new AuditDenyReasonCount(g.ApplicationId, g.DenyReason!, g.Count))
            .ToList();

        return new AuditNarrative(
            query.FromUtc,
            query.ToUtc,
            narrativeEvents,
            byApplication,
            byActor,
            byEventType,
            topDenyReasons);
    }
}

/// <summary>The window and optional filters for an audit-narrative request.</summary>
public sealed record AuditNarrativeQuery(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string? ApplicationId,
    string? ActorEmail,
    string? EventType);

/// <summary>
/// The deterministic result: the raw events in range (with real identities, for the authorized
/// auditor) plus grouped counts and a deny-reason aggregate. The AI narration is layered on top by
/// the controller and never replaces these facts.
/// </summary>
public sealed record AuditNarrative(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<AuditNarrativeEvent> Events,
    IReadOnlyList<AuditGroupCount> ByApplication,
    IReadOnlyList<AuditGroupCount> ByActor,
    IReadOnlyList<AuditGroupCount> ByEventType,
    IReadOnlyList<AuditDenyReasonCount> TopDenyReasons);

/// <summary>A single audit event with its concrete evidence. <see cref="EventId"/> is the citation.</summary>
public sealed record AuditNarrativeEvent(
    string EventId,
    string EventType,
    string? ApplicationId,
    string? ActorEmail,
    string? TargetSubjectEmail,
    DateTimeOffset Timestamp,
    string? OldValue,
    string? NewValue,
    string? Reason);

/// <summary>A grouped count (by application, actor, or event type).</summary>
public sealed record AuditGroupCount(string Key, int Count);

/// <summary>A deny-reason aggregate for one application in the window.</summary>
public sealed record AuditDenyReasonCount(string ApplicationId, string DenyReason, int Count);
