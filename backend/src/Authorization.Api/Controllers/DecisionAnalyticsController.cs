using Authorization.Api.Authorization;
using Authorization.Api.Constants;
using Authorization.Api.Governance;
using Authorization.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Controllers;

/// <summary>
/// Read-only analytics over recorded runtime decisions for an application: allow/deny mix, top
/// denial reasons, most-denied resource/action pairs, and daily volume. Purely aggregate; the
/// enforcement path is untouched. Decision recording is gated by the decision-outbox feature flag,
/// so this surface is empty until recording is enabled.
/// </summary>
[ApiController]
[Authorize(Policy = DelegatedAdminPolicyNames.AdminApi)]
[Route("v1/admin")]
public sealed class DecisionAnalyticsController : GovernanceControllerBase
{
    public DecisionAnalyticsController(AuthorizationDbContext dbContext)
        : base(dbContext)
    {
    }

    [HttpGet("applications/{applicationId}/decisions/analytics")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ReadOnlyView)]
    public async Task<ActionResult<DecisionAnalyticsResponse>> GetAnalyticsAsync(
        string applicationId,
        [FromQuery] int? windowDays,
        CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "Application was not found."));
        }

        int days = windowDays is int d && d > 0 ? Math.Min(d, 365) : 30;
        DateTimeOffset from = DateTimeOffset.UtcNow.Date.AddDays(-(days - 1));

        // Materialise the bounded in-window slice, then aggregate in memory. Postgres cannot
        // translate GroupBy(...).Select(new Record(...)) here, and the window keeps this cheap.
        var rows = await DbContext.Decisions.AsNoTracking()
            .Where(decision => decision.ApplicationId == applicationId && decision.Timestamp >= from)
            .Select(decision => new
            {
                decision.Allowed,
                decision.DenyReason,
                decision.ResourceType,
                decision.Action,
                decision.Timestamp,
            })
            .ToListAsync(cancellationToken);

        int total = rows.Count;
        int allowed = rows.Count(r => r.Allowed);
        int denied = total - allowed;

        List<DecisionCount> topDenyReasons = rows
            .Where(r => !r.Allowed && !string.IsNullOrEmpty(r.DenyReason))
            .GroupBy(r => r.DenyReason!)
            .Select(g => new DecisionCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Label, StringComparer.Ordinal)
            .Take(10)
            .ToList();

        List<DecisionCount> topDeniedResources = rows
            .Where(r => !r.Allowed)
            .GroupBy(r => $"{r.ResourceType}:{r.Action}")
            .Select(g => new DecisionCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Label, StringComparer.Ordinal)
            .Take(10)
            .ToList();

        List<DecisionDay> daily = rows
            .GroupBy(r => r.Timestamp.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
            .Select(g => new DecisionDay(g.Key, g.Count(x => x.Allowed), g.Count(x => !x.Allowed)))
            .OrderBy(d => d.Date, StringComparer.Ordinal)
            .ToList();

        return Ok(new DecisionAnalyticsResponse(days, total, allowed, denied, topDenyReasons, topDeniedResources, daily));
    }
}

public sealed record DecisionAnalyticsResponse(
    int WindowDays,
    int Total,
    int Allowed,
    int Denied,
    IReadOnlyList<DecisionCount> TopDenyReasons,
    IReadOnlyList<DecisionCount> TopDeniedResources,
    IReadOnlyList<DecisionDay> Daily);

public sealed record DecisionCount(string Label, int Count);

public sealed record DecisionDay(string Date, int Allowed, int Denied);
