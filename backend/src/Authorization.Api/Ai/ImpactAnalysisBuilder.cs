using System.Text.Json;
using Authorization.Ai;
using Authorization.Infrastructure.Persistence;
using Authorization.Infrastructure.RuntimeAuthorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Authorization.Api.Ai;

/// <summary>
/// Computes the blast radius of publishing a DRAFT policy (F5).
/// It shadow-evaluates a representative set of authorization requests against the deterministic
/// policy engine twice — once with the policy as it stands today and once with the policy applied —
/// and reports exactly which subjects flip between allow and deny. The policy is applied inside a
/// transaction that is <b>always rolled back</b>, so nothing is ever persisted and the enforcement
/// path is untouched. All output is grounded in real store data so the AI narration cites concrete
/// evidence.
/// </summary>
public sealed class ImpactAnalysisBuilder
{
    // Upper bound on the number of representative requests evaluated, to keep the analysis bounded.
    private const int MaxSamples = 50;

    // How many recent decisions to scan before de-duplication.
    private const int DecisionScanLimit = 500;

    private readonly AuthorizationDbContext dbContext;
    private readonly IAuthorizationPolicyEngine engine;
    private readonly TimeProvider timeProvider;

    public ImpactAnalysisBuilder(
        AuthorizationDbContext dbContext,
        IAuthorizationPolicyEngine engine,
        TimeProvider timeProvider)
    {
        this.dbContext = dbContext;
        this.engine = engine;
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// Builds the impact of publishing the DRAFT policy identified by <paramref name="policyKey"/>.
    /// Returns <c>null</c> when no draft with that key exists for the application.
    /// </summary>
    public async Task<ImpactAnalysisFacts?> BuildAsync(
        Guid applicationRefId,
        string applicationId,
        string policyKey,
        CancellationToken cancellationToken)
    {
        // 1 — The policy to be published (tracked, because we flip its state during the dry
        // run). Only a DRAFT policy answers the question "what changes if this policy becomes
        // live?", so only drafts are analysable.
        PolicyEntity? draft = await dbContext.Policies
            .FirstOrDefaultAsync(
                policy => policy.ApplicationRefId == applicationRefId
                    && policy.PolicyKey == policyKey
                    && policy.State == "DRAFT",
                cancellationToken);
        if (draft is null)
        {
            return null;
        }

        // 2 — The permission the policy guards, so we know which resource/action to probe.
        PermissionEntity? permission = await dbContext.Permissions
            .AsNoTracking()
            .FirstOrDefaultAsync(entity => entity.Id == draft.PermissionRefId, cancellationToken);
        if (permission is null)
        {
            return new ImpactAnalysisFacts(policyKey, draft.Effect, 0, 0, 0, SampledFromHistory: false, []);
        }

        // 3 — Representative requests: recent distinct decisions for this resource/action, with a
        // fallback to the active-assignment subject matrix when no decisions have been recorded.
        (IReadOnlyList<AuthorizeRequest> requests, bool sampledFromHistory) =
            await GatherRepresentativeRequestsAsync(applicationRefId, applicationId, permission, cancellationToken);
        if (requests.Count == 0)
        {
            return new ImpactAnalysisFacts(policyKey, draft.Effect, 0, 0, 0, sampledFromHistory, []);
        }

        // 4 — Evaluate BEFORE with the draft still unpublished.
        var before = new AuthorizeDecision[requests.Count];
        for (int i = 0; i < requests.Count; i++)
        {
            before[i] = await engine.AuthorizeAsync(requests[i], cancellationToken);
        }

        // 5 — Apply the draft, then evaluate AFTER. On a relational store the change is made inside
        // a transaction that is always rolled back, so nothing is ever persisted. On providers that
        // do not support transactions (the in-memory test store) we revert the change explicitly.
        var after = new AuthorizeDecision[requests.Count];
        string originalState = draft.State;
        DateTimeOffset? originalPublishedAt = draft.PublishedAt;
        bool useTransaction = dbContext.Database.IsRelational();
        IDbContextTransaction? transaction = useTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            draft.State = "PUBLISHED";
            draft.PublishedAt = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken);

            for (int i = 0; i < requests.Count; i++)
            {
                after[i] = await engine.AuthorizeAsync(requests[i], cancellationToken);
            }
        }
        finally
        {
            // Restore the policy's original DRAFT state — analysis must never change it.
            draft.State = originalState;
            draft.PublishedAt = originalPublishedAt;

            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();

                // Undo the in-memory mutation so the shared scoped context cannot accidentally
                // re-publish the draft later in the request pipeline.
                dbContext.Entry(draft).State = EntityState.Detached;
            }
            else
            {
                // No transaction was available, so the flip really was written — put it back.
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        // 6 — Diff: a flip is any request whose allow/deny outcome changed.
        var flips = new List<ImpactFlipFact>();
        int allowToDeny = 0;
        int denyToAllow = 0;
        for (int i = 0; i < requests.Count; i++)
        {
            if (before[i].Allowed == after[i].Allowed)
            {
                continue;
            }

            if (before[i].Allowed)
            {
                allowToDeny++;
            }
            else
            {
                denyToAllow++;
            }

            flips.Add(new ImpactFlipFact(
                SubjectEmail: requests[i].SubjectEmail,
                ResourceId: requests[i].ResourceId,
                Action: requests[i].Action,
                Before: before[i].Allowed,
                After: after[i].Allowed,
                Reason: after[i].DenyReason ?? before[i].DenyReason));
        }

        return new ImpactAnalysisFacts(
            PolicyKey: policyKey,
            Effect: draft.Effect,
            EvaluatedCount: requests.Count,
            AllowToDenyCount: allowToDeny,
            DenyToAllowCount: denyToAllow,
            SampledFromHistory: sampledFromHistory,
            Flips: flips);
    }

    private async Task<(IReadOnlyList<AuthorizeRequest> Requests, bool SampledFromHistory)> GatherRepresentativeRequestsAsync(
        Guid applicationRefId,
        string applicationId,
        PermissionEntity permission,
        CancellationToken cancellationToken)
    {
        // Primary source: real recorded decisions for the exact resource/action, most recent first.
        List<DecisionEntity> recentDecisions = await dbContext.Decisions
            .AsNoTracking()
            .Where(decision => decision.ApplicationId == applicationId
                && decision.ResourceType == permission.Resource
                && decision.Action == permission.Action)
            .OrderByDescending(decision => decision.Timestamp)
            .Take(DecisionScanLimit)
            .ToListAsync(cancellationToken);

        if (recentDecisions.Count > 0)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var requests = new List<AuthorizeRequest>();
            foreach (DecisionEntity decision in recentDecisions)
            {
                string key = string.Join(
                    '\u0001',
                    decision.SubjectType,
                    decision.SubjectEmail ?? string.Empty,
                    decision.ResourceId ?? string.Empty,
                    decision.ContextSnapshot ?? string.Empty);
                if (!seen.Add(key))
                {
                    continue;
                }

                requests.Add(new AuthorizeRequest(
                    ApplicationId: applicationId,
                    SubjectType: decision.SubjectType,
                    SubjectEmail: decision.SubjectEmail,
                    ResourceType: permission.Resource,
                    ResourceId: decision.ResourceId,
                    Action: permission.Action,
                    Context: ParseContext(decision.ContextSnapshot)));

                if (requests.Count >= MaxSamples)
                {
                    break;
                }
            }

            return (requests, true);
        }

        // Fallback: synthesize one request per distinct subject that currently holds an active
        // assignment for the application. Context is empty, so context-dependent policies surface
        // as "insufficient history" rather than false flips — the narration flags the limitation.
        DateTimeOffset now = timeProvider.GetUtcNow();
        List<string> subjectEmails = await dbContext.Assignments
            .AsNoTracking()
            .Where(assignment => assignment.ApplicationRefId == applicationRefId
                && assignment.SubjectEmail != null
                && assignment.State == "ACTIVE"
                && assignment.RevokedAt == null
                && assignment.ValidFrom <= now
                && (assignment.ValidUntil == null || assignment.ValidUntil > now))
            .Select(assignment => assignment.SubjectEmail!)
            .Distinct()
            .Take(MaxSamples)
            .ToListAsync(cancellationToken);

        var synthesized = subjectEmails
            .Select(email => new AuthorizeRequest(
                ApplicationId: applicationId,
                SubjectType: "USER",
                SubjectEmail: email,
                ResourceType: permission.Resource,
                ResourceId: null,
                Action: permission.Action,
                Context: new Dictionary<string, object?>()))
            .ToList();

        return (synthesized, false);
    }

    private static IReadOnlyDictionary<string, object?> ParseContext(string? contextSnapshot)
    {
        if (string.IsNullOrWhiteSpace(contextSnapshot))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(contextSnapshot);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, object?>();
            }

            var context = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                context[property.Name] = ConvertJsonValue(property.Value);
            }

            return context;
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    private static object? ConvertJsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetInt64(out long l) ? l : element.GetDouble(),
        _ => element.ToString(),
    };
}

/// <summary>
/// Deterministic facts describing the impact of publishing a draft policy, handed to the AI layer
/// for narration and returned to the client for the flip table.
/// </summary>
public sealed record ImpactAnalysisFacts(
    string PolicyKey,
    string Effect,
    int EvaluatedCount,
    int AllowToDenyCount,
    int DenyToAllowCount,
    bool SampledFromHistory,
    IReadOnlyList<ImpactFlipFact> Flips);
