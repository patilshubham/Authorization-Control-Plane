using Authorization.Api.Authorization;
using Authorization.Api.Constants;
using Authorization.Api.Contracts;
using Authorization.Api.Governance;
using Authorization.Infrastructure.Persistence;
using Authorization.Infrastructure.RuntimeAuthorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Controllers;

/// <summary>
/// Read-only, cross-cutting governance surfaces: the platform overview, audit
/// event feed, access simulator and user access directory.
/// </summary>
[ApiController]
[Authorize(Policy = DelegatedAdminPolicyNames.AdminApi)]
[Route("v1/admin")]
public sealed class GovernanceInsightsController : GovernanceControllerBase
{
    private readonly IAuthorizationPolicyEngine policyEngine;
    private readonly DelegatedAdminAuthorizationService authorizationService;

    public GovernanceInsightsController(
        AuthorizationDbContext dbContext,
        IAuthorizationPolicyEngine policyEngine,
        DelegatedAdminAuthorizationService authorizationService)
        : base(dbContext)
    {
        this.policyEngine = policyEngine;
        this.authorizationService = authorizationService;
    }

    [HttpGet("audit-events")]
    public async Task<ActionResult<PagedResult<AuditEventResponse>>> GetAuditEventsAsync([FromQuery] string? applicationId, [FromQuery] string? actorEmail, [FromQuery] string? q, [FromQuery] string? category, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        (IQueryable<AuditEventEntity>? scoped, ActionResult? denied) = await BuildScopedAuditQueryAsync(applicationId, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        IQueryable<AuditEventEntity> query = scoped!;

        if (!string.IsNullOrWhiteSpace(actorEmail))
        {
            query = query.Where(entity => entity.ActorEmail == actorEmail);
        }

        // Free-text search across the columns surfaced in the feed. ToLower()+Contains
        // is translated to SQL by Npgsql and also runs on the in-memory test provider,
        // so it works everywhere (unlike EF.Functions.ILike).
        if (!string.IsNullOrWhiteSpace(q))
        {
            string needle = q.Trim().ToLowerInvariant();
            query = query.Where(entity =>
                (entity.EventType != null && entity.EventType.ToLower().Contains(needle))
                || (entity.ActorEmail != null && entity.ActorEmail.ToLower().Contains(needle))
                || (entity.ApplicationId != null && entity.ApplicationId.ToLower().Contains(needle))
                || (entity.TargetSubjectEmail != null && entity.TargetSubjectEmail.ToLower().Contains(needle)));
        }

        // Category maps to an event-type prefix taxonomy. Keep this in sync with the
        // frontend categoryOf() in frontend/src/workspace/activity.ts.
        query = ApplyCategoryFilter(query, category);

        // The audit feed grows without bound, so it is always paged (default 50/page) rather than
        // silently truncated. Total lets the UI show "N of M" and a real page count.
        int size = pageSize is int s && s > 0 ? Math.Min(s, PageRequest.MaxPageSize) : 50;
        int pageNumber = page is int p && p > 0 ? p : 1;
        int total = await query.CountAsync(cancellationToken);
        List<AuditEventEntity> events = await query
            .OrderByDescending(entity => entity.Timestamp)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);
        return Ok(new PagedResult<AuditEventResponse>(events.Select(AuditEventResponse.From).ToList(), pageNumber, size, total));
    }

    [HttpGet("audit-events/summary")]
    public async Task<ActionResult<AuditActivitySummaryResponse>> GetAuditActivitySummaryAsync([FromQuery] string? applicationId, [FromQuery] int? days, CancellationToken cancellationToken)
    {
        (IQueryable<AuditEventEntity>? scoped, ActionResult? denied) = await BuildScopedAuditQueryAsync(applicationId, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        // Bound the window so the aggregate stays cheap regardless of total volume.
        int windowDays = days is int d && d > 0 ? Math.Min(d, 370) : 112;
        DateTimeOffset from = DateTimeOffset.UtcNow.Date.AddDays(-(windowDays - 1));
        // Only the timestamps are needed; materialising the in-window slice keeps this
        // bounded by the window (not the whole table) and lets us bucket by UTC date.
        List<DateTimeOffset> timestamps = await scoped!
            .Where(entity => entity.Timestamp >= from)
            .Select(entity => entity.Timestamp)
            .ToListAsync(cancellationToken);
        List<AuditActivityDay> daysList = timestamps
            .GroupBy(ts => ts.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
            .Select(g => new AuditActivityDay(g.Key, g.Count()))
            .OrderBy(day => day.Date, StringComparer.Ordinal)
            .ToList();
        return Ok(new AuditActivitySummaryResponse(timestamps.Count, daysList));
    }

    // Builds the audit query already scoped to the applications the caller may audit.
    // Returns a 403 result instead of a query when an explicit applicationId is off-limits.
    private async Task<(IQueryable<AuditEventEntity>? Query, ActionResult? Denied)> BuildScopedAuditQueryAsync(string? applicationId, CancellationToken cancellationToken)
    {
        IQueryable<AuditEventEntity> query = DbContext.AuditEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(applicationId))
        {
            if (!authorizationService.CanAccessAllApplications(User))
            {
                string? tenantId = await TenantIdForApplicationAsync(applicationId, cancellationToken);
                if (!authorizationService.IsAuthorized(User, applicationId, tenantId, DelegatedAdminCapability.ViewAudit))
                {
                    return (null, StatusCode(StatusCodes.Status403Forbidden, Error(GovernanceErrorCodes.Forbidden, "You are not permitted to view this application's audit events.")));
                }
            }

            query = query.Where(entity => entity.ApplicationId == applicationId);
        }
        else if (!authorizationService.CanAccessAllApplications(User))
        {
            // Scope results to only the applications the caller is permitted to audit — whether by an
            // application-scoped role or a tenant-scoped role over the owning tenant.
            Dictionary<Guid, string> tenantIdByRefId = await TenantIdByRefIdAsync(cancellationToken);
            List<(string ApplicationId, Guid TenantRefId)> allApplications = await DbContext.Applications
                .AsNoTracking()
                .Select(entity => new ValueTuple<string, Guid>(entity.ApplicationId, entity.TenantRefId))
                .ToListAsync(cancellationToken);
            List<string> auditableApplicationIds = allApplications
                .Where(application => authorizationService.IsAuthorized(
                    User,
                    application.ApplicationId,
                    tenantIdByRefId.GetValueOrDefault(application.TenantRefId),
                    DelegatedAdminCapability.ViewAudit))
                .Select(application => application.ApplicationId)
                .ToList();

            query = query.Where(entity => entity.ApplicationId != null && auditableApplicationIds.Contains(entity.ApplicationId));
        }

        return (query, null);
    }

    // Filters the audit query by the UI category taxonomy (event-type prefixes).
    // Mirrors categoryOf() in frontend/src/workspace/activity.ts.
    private static IQueryable<AuditEventEntity> ApplyCategoryFilter(IQueryable<AuditEventEntity> query, string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return query;
        }

        return category.Trim().ToLowerInvariant() switch
        {
            "mapping" => query.Where(e => e.EventType.ToUpper().StartsWith("ROLE_PERMISSION")),
            "role" => query.Where(e => e.EventType.ToUpper().StartsWith("ROLE") && !e.EventType.ToUpper().StartsWith("ROLE_PERMISSION")),
            "permission" => query.Where(e => e.EventType.ToUpper().StartsWith("PERMISSION")),
            "policy" => query.Where(e => e.EventType.ToUpper().StartsWith("POLICY")),
            "assignment" => query.Where(e => e.EventType.ToUpper().StartsWith("ASSIGNMENT")),
            "application" => query.Where(e => e.EventType.ToUpper().StartsWith("APPLICATION")),
            "identity" => query.Where(e => e.EventType.ToUpper().StartsWith("OIDC") || e.EventType.ToUpper().Contains("PROVIDER")),
            "other" => query.Where(e =>
                !e.EventType.ToUpper().StartsWith("ROLE")
                && !e.EventType.ToUpper().StartsWith("PERMISSION")
                && !e.EventType.ToUpper().StartsWith("POLICY")
                && !e.EventType.ToUpper().StartsWith("ASSIGNMENT")
                && !e.EventType.ToUpper().StartsWith("APPLICATION")
                && !e.EventType.ToUpper().StartsWith("OIDC")
                && !e.EventType.ToUpper().Contains("PROVIDER")),
            _ => query,
        };
    }


    [HttpPost("simulator/authorize")]
    public async Task<ActionResult<AuthorizeResponse>> SimulateAsync([FromBody] SimulatorAuthorizeRequest request, CancellationToken cancellationToken)
    {
        if (!authorizationService.CanAccessAllApplications(User)
            && !authorizationService.IsAuthorized(User, request.ApplicationId, DelegatedAdminCapability.ReadOnlyView))
        {
            return StatusCode(StatusCodes.Status403Forbidden, Error(GovernanceErrorCodes.Forbidden, "You are not permitted to run simulations for this application."));
        }

        AuthorizeDecision decision = await policyEngine.AuthorizeAsync(new AuthorizeRequest(
            request.ApplicationId,
            request.SubjectType,
            request.SubjectEmail,
            request.ResourceType,
            request.ResourceId,
            request.Action,
            request.Context ?? new Dictionary<string, object?>()), cancellationToken);

        return AuthorizeResponse.FromDecision(decision);
    }

    [HttpGet("overview")]
    public async Task<ActionResult<PlatformOverviewResponse>> GetPlatformOverviewAsync(CancellationToken cancellationToken)
    {
        bool canAccessAll = authorizationService.CanAccessAllApplications(User);
        List<ApplicationEntity> apps = await DbContext.Applications.AsNoTracking().ToListAsync(cancellationToken);
        if (!canAccessAll)
        {
            apps = apps.Where(a => authorizationService.IsAuthorized(User, a.ApplicationId, DelegatedAdminCapability.ReadOnlyView)).ToList();
        }
        List<string> appIds = apps.Select(a => a.ApplicationId).ToList();
        List<Guid> appGuids = apps.Select(a => a.Id).ToList();

        List<TenantEntity> tenants = await DbContext.Tenants.AsNoTracking().ToListAsync(cancellationToken);
        Dictionary<Guid, string> tenantIdByGuid = tenants.ToDictionary(t => t.Id, t => t.TenantId);
        Dictionary<Guid, string> tenantNameByGuid = tenants.ToDictionary(t => t.Id, t => t.Name);
        int tenantCount = canAccessAll
            ? tenants.Count
            : apps.Select(a => a.TenantRefId).Distinct().Count();

        int roleCount = await DbContext.Roles.AsNoTracking().CountAsync(r => appGuids.Contains(r.ApplicationRefId), cancellationToken);
        int permissionCount = await DbContext.Permissions.AsNoTracking().CountAsync(p => appGuids.Contains(p.ApplicationRefId), cancellationToken);
        int policyCount = await DbContext.Policies.AsNoTracking().CountAsync(p => appGuids.Contains(p.ApplicationRefId), cancellationToken);
        var assignmentRows = await DbContext.Assignments.AsNoTracking()
            .Where(x => appGuids.Contains(x.ApplicationRefId))
            .Select(x => new { x.State, x.ValidUntil, x.RevokedAt })
            .ToListAsync(cancellationToken);
        int assignmentCount = assignmentRows.Count;
        int activeAssignmentCount = assignmentRows.Count(r => AssignmentDisplayStatus.Resolve(r.State, r.ValidUntil, r.RevokedAt) == AssignmentDisplayStatus.Active);

        Dictionary<string, int> applicationsByRisk = apps
            .GroupBy(a => string.IsNullOrWhiteSpace(a.RiskLevel) ? RiskLevel.Medium : a.RiskLevel.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.Count());

        List<TenantApplicationCount> applicationsByTenant = apps
            .GroupBy(a => a.TenantRefId)
            .Select(g => new TenantApplicationCount(
                tenantIdByGuid.TryGetValue(g.Key, out string? tid) ? tid : g.Key.ToString(),
                tenantNameByGuid.TryGetValue(g.Key, out string? n) ? n : g.Key.ToString(),
                g.Count()))
            .OrderByDescending(x => x.ApplicationCount)
            .ThenBy(x => x.TenantName)
            .ToList();

        IQueryable<AuditEventEntity> auditQuery = DbContext.AuditEvents.AsNoTracking();
        if (!canAccessAll)
        {
            auditQuery = auditQuery.Where(e => e.ApplicationId != null && appIds.Contains(e.ApplicationId));
        }
        List<AuditEventEntity> recentAudit = await auditQuery.OrderByDescending(e => e.Timestamp).Take(10).ToListAsync(cancellationToken);

        return new PlatformOverviewResponse(
            tenantCount,
            apps.Count,
            roleCount,
            permissionCount,
            policyCount,
            assignmentCount,
            activeAssignmentCount,
            applicationsByRisk,
            applicationsByTenant,
            recentAudit.Select(AuditEventResponse.From).ToList());
    }

    [HttpGet("users")]
    public async Task<ActionResult<PagedResult<UserDirectoryEntry>>> GetUsersAsync([FromQuery] string? q, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        List<Guid> appGuids = await AccessibleApplicationGuidsAsync(cancellationToken);
        var rows = await DbContext.Assignments.AsNoTracking()
            .Where(a => appGuids.Contains(a.ApplicationRefId) && a.SubjectEmail != null)
            .Select(a => new { a.SubjectEmail, a.ApplicationRefId, a.State, a.ValidUntil, a.RevokedAt })
            .ToListAsync(cancellationToken);

        List<UserDirectoryEntry> users = rows
            .GroupBy(r => r.SubjectEmail!)
            .Select(g => new UserDirectoryEntry(
                g.Key,
                g.Select(x => x.ApplicationRefId).Distinct().Count(),
                g.Count(),
                g.Count(x => AssignmentDisplayStatus.Resolve(x.State, x.ValidUntil, x.RevokedAt) == AssignmentDisplayStatus.Active),
                g.Count(x => AssignmentDisplayStatus.Resolve(x.State, x.ValidUntil, x.RevokedAt) == AssignmentDisplayStatus.Expired),
                g.Count(x => AssignmentDisplayStatus.Resolve(x.State, x.ValidUntil, x.RevokedAt) == AssignmentDisplayStatus.Revoked)))
            .OrderBy(u => u.Email)
            .ToList();

        if (!string.IsNullOrWhiteSpace(q))
        {
            string needle = q.Trim();
            users = users.Where(u => u.Email.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        PageRequest paging = PageRequest.From(page, pageSize);
        int total = users.Count;
        List<UserDirectoryEntry> pageItems = paging.Enabled
            ? users.Skip(paging.Skip).Take(paging.PageSize).ToList()
            : users;
        return Ok(new PagedResult<UserDirectoryEntry>(pageItems, paging.Enabled ? paging.Page : 1, paging.Enabled ? paging.PageSize : total, total));
    }

    [HttpGet("users/{email}")]
    public async Task<ActionResult<UserAccessResponse>> GetUserAsync(string email, CancellationToken cancellationToken)
    {
        List<Guid> appGuids = await AccessibleApplicationGuidsAsync(cancellationToken);
        List<AssignmentEntity> assignments = await DbContext.Assignments.AsNoTracking()
            .Where(a => a.SubjectEmail == email && appGuids.Contains(a.ApplicationRefId))
            .ToListAsync(cancellationToken);

        Dictionary<Guid, string> appIdByGuid = await DbContext.Applications.AsNoTracking()
            .Where(a => appGuids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.ApplicationId, cancellationToken);
        Dictionary<Guid, string> roleKeyByGuid = await DbContext.Roles.AsNoTracking()
            .Where(r => appGuids.Contains(r.ApplicationRefId))
            .ToDictionaryAsync(r => r.Id, r => r.RoleKey, cancellationToken);

        List<UserAccessAssignment> rows = assignments
            .Select(a => new UserAccessAssignment(
                appIdByGuid.GetValueOrDefault(a.ApplicationRefId, string.Empty),
                roleKeyByGuid.GetValueOrDefault(a.RoleRefId, string.Empty),
                AssignmentDisplayStatus.Resolve(a.State, a.ValidUntil, a.RevokedAt),
                a.ValidUntil))
            .OrderBy(r => r.ApplicationId).ThenBy(r => r.RoleKey)
            .ToList();

        return new UserAccessResponse(email, rows);
    }

    private async Task<List<Guid>> AccessibleApplicationGuidsAsync(CancellationToken cancellationToken)
    {
        List<(Guid Id, string ApplicationId, Guid TenantRefId)> all = await DbContext.Applications.AsNoTracking()
            .Select(a => new ValueTuple<Guid, string, Guid>(a.Id, a.ApplicationId, a.TenantRefId))
            .ToListAsync(cancellationToken);
        if (authorizationService.CanAccessAllApplications(User))
        {
            return all.Select(a => a.Id).ToList();
        }

        Dictionary<Guid, string> tenantIdByRefId = await TenantIdByRefIdAsync(cancellationToken);
        return all
            .Where(a => authorizationService.IsAuthorized(
                User,
                a.ApplicationId,
                tenantIdByRefId.GetValueOrDefault(a.TenantRefId),
                DelegatedAdminCapability.ReadOnlyView))
            .Select(a => a.Id)
            .ToList();
    }

    // Maps every tenant's surrogate guid to its business id, for resolving an application's owning tenant.
    private Task<Dictionary<Guid, string>> TenantIdByRefIdAsync(CancellationToken cancellationToken)
        => DbContext.Tenants.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.TenantId, cancellationToken);

    // Resolves a single application's owning tenant business id, or null when the application does not exist.
    private Task<string?> TenantIdForApplicationAsync(string applicationId, CancellationToken cancellationToken)
        => DbContext.Applications.AsNoTracking()
            .Where(a => a.ApplicationId == applicationId)
            .Join(DbContext.Tenants.AsNoTracking(), a => a.TenantRefId, t => t.Id, (a, t) => t.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
}
