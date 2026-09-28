using Authorization.Api.Authorization;
using Authorization.Api.Constants;
using Authorization.Api.Contracts;
using Authorization.Api.Governance;
using Authorization.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Controllers;

[ApiController]
[Authorize(Policy = DelegatedAdminPolicyNames.AdminApi)]
[Route("v1/admin")]
public sealed class TenantsController : GovernanceControllerBase
{
    private readonly DelegatedAdminAuthorizationService authorizationService;

    public TenantsController(AuthorizationDbContext dbContext, DelegatedAdminAuthorizationService authorizationService)
        : base(dbContext)
    {
        this.authorizationService = authorizationService;
    }

    [HttpGet("tenants")]
    public async Task<IReadOnlyList<TenantResponse>> GetTenantsAsync(CancellationToken cancellationToken)
    {
        List<TenantEntity> tenants = await DbContext.Tenants.AsNoTracking().OrderBy(entity => entity.Name).ToListAsync(cancellationToken);
        if (!authorizationService.CanAccessAllApplications(User))
        {
            HashSet<Guid> accessibleTenantRefs = await AccessibleTenantRefIdsAsync(cancellationToken);
            tenants = tenants.Where(tenant => accessibleTenantRefs.Contains(tenant.Id)).ToList();
        }

        return tenants.Select(TenantResponse.From).ToList();
    }

    [HttpPost("tenants")]
    [Authorize(Policy = DelegatedAdminPolicyNames.PlatformAdmin)]
    public async Task<IActionResult> CreateTenantAsync([FromBody] CreateTenantRequest request, CancellationToken cancellationToken)
    {
        string tenantId = request.TenantId.Trim();
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "tenantId is required."));
        }

        if (RequireText(request.Name, "Name") is { } nameError)
        {
            return nameError;
        }

        if (await DbContext.Tenants.AnyAsync(entity => entity.TenantId == tenantId, cancellationToken))
        {
            return Conflict(Error(GovernanceErrorCodes.TenantExists, "Tenant already exists."));
        }

        var entity = new TenantEntity
        {
            TenantId = tenantId,
            Name = request.Name,
            Description = request.Description,
            Status = GovernanceStatus.Active,
            CreatedBy = Actor,
        };

        DbContext.Tenants.Add(entity);
        object created = new { entity.TenantId, entity.Name, entity.Description, entity.Status };
        await SaveGovernanceMutationAsync(AuditEventTypes.TenantCreated, null, null, created, cancellationToken);
        return Created($"/v1/admin/tenants/{entity.TenantId}", TenantResponse.From(entity));
    }

    [HttpPut("tenants/{tenantId}")]
    [Authorize(Policy = DelegatedAdminPolicyNames.PlatformAdmin)]
    public async Task<IActionResult> UpdateTenantAsync(string tenantId, [FromBody] UpdateTenantRequest request, CancellationToken cancellationToken)
    {
        TenantEntity? entity = await DbContext.Tenants.FirstOrDefaultAsync(tenant => tenant.TenantId == tenantId, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.TenantNotFound, "Tenant was not found."));
        }

        if (RequireText(request.Name, "Name") is { } nameError)
        {
            return nameError;
        }

        if (request.Status is not (null or GovernanceStatus.Active or GovernanceStatus.Disabled or GovernanceStatus.Archived))
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "status must be ACTIVE, DISABLED or ARCHIVED."));
        }

        object oldValue = new { entity.Name, entity.Description, entity.Status };
        entity.Name = request.Name;
        entity.Description = request.Description;
        entity.Status = request.Status ?? entity.Status;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedBy = Actor;
        await SaveGovernanceMutationAsync(AuditEventTypes.TenantUpdated, null, null, oldValue, new { entity.Name, entity.Description, entity.Status }, cancellationToken);
        return Ok(TenantResponse.From(entity));
    }

    [HttpDelete("tenants/{tenantId}")]
    [Authorize(Policy = DelegatedAdminPolicyNames.PlatformAdmin)]
    public async Task<IActionResult> DeleteTenantAsync(string tenantId, CancellationToken cancellationToken)
    {
        TenantEntity? entity = await DbContext.Tenants.FirstOrDefaultAsync(tenant => tenant.TenantId == tenantId, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.TenantNotFound, "Tenant was not found."));
        }

        bool hasApplications = await DbContext.Applications.AnyAsync(application => application.TenantRefId == entity.Id, cancellationToken);
        if (hasApplications)
        {
            return Conflict(Error(GovernanceErrorCodes.TenantInUse, "This tenant still owns one or more applications. Re-parent or remove them before deleting."));
        }

        DbContext.Tenants.Remove(entity);
        await SaveGovernanceMutationAsync(AuditEventTypes.TenantDeleted, null, null, new { entity.TenantId, entity.Name }, newValue: null, cancellationToken);
        return NoContent();
    }

    [HttpGet("tenants/{tenantId}")]
    public async Task<IActionResult> GetTenantAsync(string tenantId, CancellationToken cancellationToken)
    {
        TenantEntity? tenant = await DbContext.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.TenantId == tenantId, cancellationToken);
        if (tenant is null)
        {
            return NotFound(Error(GovernanceErrorCodes.TenantNotFound, "Tenant was not found."));
        }

        // A delegated (application-scoped) admin has no platform visibility: a
        // tenant they cannot reach through one of their applications is treated
        // as not found so its existence is never disclosed.
        if (!authorizationService.CanAccessAllApplications(User))
        {
            HashSet<Guid> accessibleTenantRefs = await AccessibleTenantRefIdsAsync(cancellationToken);
            if (!accessibleTenantRefs.Contains(tenant.Id))
            {
                return NotFound(Error(GovernanceErrorCodes.TenantNotFound, "Tenant was not found."));
            }
        }

        List<ApplicationEntity> apps = await DbContext.Applications.AsNoTracking()
            .Where(a => a.TenantRefId == tenant.Id)
            .OrderBy(a => a.Name)
            .ToListAsync(cancellationToken);
        List<Guid> appGuids = apps.Select(a => a.Id).ToList();

        Dictionary<Guid, int> roleCounts = (await DbContext.Roles.AsNoTracking()
                .Where(r => appGuids.Contains(r.ApplicationRefId))
                .Select(r => r.ApplicationRefId)
                .ToListAsync(cancellationToken))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());
        var assignmentRows = await DbContext.Assignments.AsNoTracking()
            .Where(x => appGuids.Contains(x.ApplicationRefId))
            .Select(x => new { x.ApplicationRefId, x.State, x.ValidUntil, x.RevokedAt })
            .ToListAsync(cancellationToken);

        List<TenantApplicationSummary> appSummaries = apps.Select(a => new TenantApplicationSummary(
                a.ApplicationId,
                a.Name,
                a.Status,
                a.RiskLevel,
                roleCounts.TryGetValue(a.Id, out int rc) ? rc : 0,
                assignmentRows.Count(r => r.ApplicationRefId == a.Id),
                assignmentRows.Count(r => r.ApplicationRefId == a.Id && AssignmentDisplayStatus.Resolve(r.State, r.ValidUntil, r.RevokedAt) == AssignmentDisplayStatus.Active)))
            .ToList();

        var rollup = new TenantRollup(
            apps.Count,
            appSummaries.Sum(s => s.RoleCount),
            assignmentRows.Count,
            assignmentRows.Count(r => AssignmentDisplayStatus.Resolve(r.State, r.ValidUntil, r.RevokedAt) == AssignmentDisplayStatus.Active));

        return Ok(new TenantDetailResponse(TenantResponse.From(tenant), appSummaries, rollup));
    }

    // The set of tenant primary keys that own at least one application the
    // caller can read. Used to scope tenant visibility for delegated admins.
    private async Task<HashSet<Guid>> AccessibleTenantRefIdsAsync(CancellationToken cancellationToken)
    {
        List<ApplicationEntity> applications = await DbContext.Applications.AsNoTracking().ToListAsync(cancellationToken);
        return applications
            .Where(application => authorizationService.IsAuthorized(User, application.ApplicationId, DelegatedAdminCapability.ReadOnlyView))
            .Select(application => application.TenantRefId)
            .ToHashSet();
    }
}
