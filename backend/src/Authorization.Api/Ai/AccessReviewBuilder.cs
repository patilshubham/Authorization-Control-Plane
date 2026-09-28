using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Ai;

/// <summary>
/// Gathers read-only, deterministic facts for an access-certification (recertification) review of a
/// single subject across the applications in scope: every active grant, its effective permissions,
/// a last-used signal drawn from recorded decisions, a dormancy flag, a peer-comparison count, and a
/// concrete keep/revoke/review recommendation. Mirrors <see cref="DecisionDiagnosticsBuilder"/> — all
/// queries are read-only and nothing here mutates state or participates in enforcement. The AI layer
/// only narrates and prioritizes these facts; it never changes a recommendation.
/// </summary>
public sealed class AccessReviewBuilder
{
    // A grant older than this with no recorded use is treated as dormant; younger grants are given
    // the benefit of the doubt (too new to have been exercised).
    private const int DormantDays = 60;

    private readonly AuthorizationDbContext dbContext;
    private readonly TimeProvider timeProvider;

    public AccessReviewBuilder(AuthorizationDbContext dbContext, TimeProvider timeProvider)
    {
        this.dbContext = dbContext;
        this.timeProvider = timeProvider;
    }

    public async Task<AccessReview> BuildAsync(
        string subjectEmail,
        IReadOnlyList<AccessReviewScope> scope,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        var items = new List<AccessReviewItem>();

        foreach (AccessReviewScope app in scope)
        {
            // 1 — Every active USER assignment in the app (the subject's plus peers', for comparison).
            List<AssignmentEntity> appAssignments = await dbContext.Assignments
                .AsNoTracking()
                .Where(assignment => assignment.ApplicationRefId == app.ApplicationRefId
                    && assignment.SubjectType == "USER"
                    && assignment.SubjectEmail != null
                    && assignment.State == "ACTIVE"
                    && assignment.RevokedAt == null
                    && assignment.ValidFrom <= now
                    && (assignment.ValidUntil == null || assignment.ValidUntil > now))
                .ToListAsync(cancellationToken);

            List<AssignmentEntity> subjectAssignments = appAssignments
                .Where(assignment => string.Equals(assignment.SubjectEmail, subjectEmail, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (subjectAssignments.Count == 0)
            {
                continue;
            }

            // Peer comparison: how many other distinct users hold each role in this application.
            Dictionary<Guid, int> peerCountByRole = appAssignments
                .Where(assignment => !string.Equals(assignment.SubjectEmail, subjectEmail, StringComparison.OrdinalIgnoreCase))
                .GroupBy(assignment => assignment.RoleRefId)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(assignment => assignment.SubjectEmail!.ToLowerInvariant())
                        .Distinct()
                        .Count());

            // 2 — Roles, published role→permission mappings, and permission metadata for the app.
            Dictionary<Guid, RoleEntity> rolesById = await dbContext.Roles
                .AsNoTracking()
                .Where(role => role.ApplicationRefId == app.ApplicationRefId)
                .ToDictionaryAsync(role => role.Id, cancellationToken);

            List<RolePermissionEntity> publishedMappings = await dbContext.RolePermissions
                .AsNoTracking()
                .Where(mapping => mapping.ApplicationRefId == app.ApplicationRefId && mapping.State == "PUBLISHED")
                .ToListAsync(cancellationToken);

            Dictionary<Guid, PermissionEntity> permissionsById = await dbContext.Permissions
                .AsNoTracking()
                .Where(permission => permission.ApplicationRefId == app.ApplicationRefId && permission.Status == "ACTIVE")
                .ToDictionaryAsync(permission => permission.Id, cancellationToken);

            Dictionary<Guid, List<PermissionEntity>> permissionsByRole = publishedMappings
                .Where(mapping => permissionsById.ContainsKey(mapping.PermissionRefId))
                .GroupBy(mapping => mapping.RoleRefId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(mapping => permissionsById[mapping.PermissionRefId]).ToList());

            // 3 — Last-used signal from recorded allowed decisions for this subject in this app.
            List<DecisionFact> allowedDecisions = await dbContext.Decisions
                .AsNoTracking()
                .Where(decision => decision.ApplicationId == app.ApplicationId
                    && decision.SubjectEmail == subjectEmail
                    && decision.Allowed)
                .Select(decision => new DecisionFact(decision.Timestamp, decision.MatchedRoles))
                .ToListAsync(cancellationToken);

            foreach (AssignmentEntity assignment in subjectAssignments)
            {
                if (!rolesById.TryGetValue(assignment.RoleRefId, out RoleEntity? role))
                {
                    continue;
                }

                List<PermissionEntity> rolePermissions = permissionsByRole.TryGetValue(role.Id, out List<PermissionEntity>? perms)
                    ? perms
                    : [];
                List<string> permissionKeys = rolePermissions
                    .Select(permission => permission.PermissionKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                DateTimeOffset? lastUsedAt = allowedDecisions
                    .Where(decision => decision.MatchedRoles.Contains(role.RoleKey, StringComparer.OrdinalIgnoreCase))
                    .Select(decision => (DateTimeOffset?)decision.Timestamp)
                    .DefaultIfEmpty(null)
                    .Max();
                int? lastUsedDaysAgo = lastUsedAt is DateTimeOffset used ? (int)(now - used).TotalDays : null;

                int grantAgeDays = (int)(now - assignment.ValidFrom).TotalDays;
                bool hadTimeToBeUsed = grantAgeDays > DormantDays;
                bool dormant = hadTimeToBeUsed && (lastUsedAt is null || lastUsedDaysAgo > DormantDays);

                int peerCount = peerCountByRole.TryGetValue(role.Id, out int count) ? count : 0;

                (string recommendation, string reason) = Recommend(role.Privileged, dormant, lastUsedDaysAgo, peerCount, hadTimeToBeUsed);

                items.Add(new AccessReviewItem(
                    Id: assignment.Id.ToString("N"),
                    ApplicationId: app.ApplicationId,
                    RoleKey: role.RoleKey,
                    RoleName: role.Name,
                    Privileged: role.Privileged,
                    RiskLevel: role.RiskLevel,
                    Status: assignment.State,
                    ValidFrom: assignment.ValidFrom,
                    ValidUntil: assignment.ValidUntil,
                    Source: assignment.Source,
                    Reason: assignment.Reason,
                    LastUsedAt: lastUsedAt,
                    LastUsedDaysAgo: lastUsedDaysAgo,
                    Dormant: dormant,
                    PeerCount: peerCount,
                    Permissions: permissionKeys,
                    Recommendation: recommendation,
                    RecommendationReason: reason));
            }
        }

        // Surface anomalies first: review before revoke before keep, privileged before not, then by app/role.
        List<AccessReviewItem> ordered = items
            .OrderBy(item => RecommendationRank(item.Recommendation))
            .ThenByDescending(item => item.Privileged)
            .ThenBy(item => item.ApplicationId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.RoleKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AccessReview(subjectEmail, ordered);
    }

    // Deterministic recommendation grounded only in the gathered signals. The AI layer explains this
    // decision but never overrides it.
    private static (string Recommendation, string Reason) Recommend(
        bool privileged,
        bool dormant,
        int? lastUsedDaysAgo,
        int peerCount,
        bool hadTimeToBeUsed)
    {
        if (dormant && privileged)
        {
            return ("REVIEW", $"Privileged role not exercised in over {DormantDays} days.");
        }

        if (dormant)
        {
            return ("REVOKE", $"Not exercised in over {DormantDays} days.");
        }

        if (privileged && peerCount == 0)
        {
            return ("REVIEW", "Privileged role held by no other user (possible outlier).");
        }

        if (!hadTimeToBeUsed)
        {
            return ("KEEP", "Recently granted; too new to judge by usage.");
        }

        return lastUsedDaysAgo is int days
            ? ("KEEP", $"Actively used (last used {days} days ago).")
            : ("KEEP", "Within retention window.");
    }

    private static int RecommendationRank(string recommendation) => recommendation switch
    {
        "REVIEW" => 0,
        "REVOKE" => 1,
        _ => 2,
    };

    private readonly record struct DecisionFact(DateTimeOffset Timestamp, string[] MatchedRoles);
}

/// <summary>One application the caller may review, pairing its internal ref id with its public id.</summary>
public sealed record AccessReviewScope(Guid ApplicationRefId, string ApplicationId);

/// <summary>
/// The deterministic result of a subject access review. <see cref="SubjectEmail"/> is returned to the
/// (same-origin, authorized) browser for display; it is never included in any AI prompt.
/// </summary>
public sealed record AccessReview(
    string SubjectEmail,
    IReadOnlyList<AccessReviewItem> Items);

/// <summary>A single active grant under review with its deterministic facts and recommendation.</summary>
public sealed record AccessReviewItem(
    string Id,
    string ApplicationId,
    string RoleKey,
    string RoleName,
    bool Privileged,
    string RiskLevel,
    string Status,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidUntil,
    string Source,
    string? Reason,
    DateTimeOffset? LastUsedAt,
    int? LastUsedDaysAgo,
    bool Dormant,
    int PeerCount,
    IReadOnlyList<string> Permissions,
    string Recommendation,
    string RecommendationReason);
