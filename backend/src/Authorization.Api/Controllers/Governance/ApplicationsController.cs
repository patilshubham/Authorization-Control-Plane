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
public sealed class ApplicationsController : GovernanceControllerBase
{
    private readonly DelegatedAdminAuthorizationService authorizationService;

    public ApplicationsController(
        AuthorizationDbContext dbContext,
        DelegatedAdminAuthorizationService authorizationService)
        : base(dbContext)
    {
        this.authorizationService = authorizationService;
    }

    [HttpGet("applications")]
    public async Task<PagedResult<ApplicationResponse>> GetApplicationsAsync(
        [FromQuery] string? tenantId,
        [FromQuery] string? status,
        [FromQuery] string? riskLevel,
        [FromQuery] string? q,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        List<ApplicationEntity> applications = await DbContext.Applications.AsNoTracking().OrderBy(entity => entity.ApplicationId).ToListAsync(cancellationToken);
        Dictionary<Guid, string> tenantIdByGuid = await DbContext.Tenants.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.TenantId, cancellationToken);

        if (!authorizationService.CanAccessAllApplications(User))
        {
            // A delegated admin sees only the applications it is authorized for, whether by an
            // application-scoped role or a tenant-scoped role over the owning tenant.
            applications = applications
                .Where(application => authorizationService.IsAuthorized(
                    User,
                    application.ApplicationId,
                    tenantIdByGuid.GetValueOrDefault(application.TenantRefId),
                    DelegatedAdminCapability.ReadOnlyView))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            applications = applications.Where(a => tenantIdByGuid.TryGetValue(a.TenantRefId, out string? t) && string.Equals(t, tenantId, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            applications = applications.Where(a => string.Equals(a.Status, status, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(riskLevel))
        {
            applications = applications.Where(a => string.Equals(a.RiskLevel, riskLevel, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            string needle = q.Trim();
            applications = applications
                .Where(a => a.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || a.ApplicationId.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (page is int p && pageSize is int size && p > 0 && size > 0)
        {
            List<ApplicationResponse> pageItems = applications
                .Skip((p - 1) * size)
                .Take(size)
                .Select(a => ApplicationResponse.From(a, tenantIdByGuid.GetValueOrDefault(a.TenantRefId)))
                .ToList();
            return new PagedResult<ApplicationResponse>(pageItems, p, size, applications.Count);
        }

        List<ApplicationResponse> items = applications
            .Select(a => ApplicationResponse.From(a, tenantIdByGuid.GetValueOrDefault(a.TenantRefId)))
            .ToList();
        return new PagedResult<ApplicationResponse>(items, 1, items.Count, items.Count);
    }

    [HttpPost("applications")]
    [Authorize(Policy = DelegatedAdminPolicyNames.PlatformAdmin)]
    public async Task<IActionResult> CreateApplicationAsync([FromBody] CreateApplicationRequest request, CancellationToken cancellationToken)
    {
        if (RequireText(request.ApplicationId, "Application id") is { } applicationIdError)
        {
            return applicationIdError;
        }

        if (RequireText(request.Name, "Name") is { } nameError)
        {
            return nameError;
        }

        if (await ApplicationExistsAsync(request.ApplicationId, cancellationToken))
        {
            return Conflict(Error(GovernanceErrorCodes.ApplicationExists, "Application already exists."));
        }

        string tenantId = (request.TenantId ?? string.Empty).Trim();
        Guid? tenantRefId = await DbContext.Tenants.Where(entity => entity.TenantId == tenantId).Select(entity => (Guid?)entity.Id).FirstOrDefaultAsync(cancellationToken);
        if (tenantRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.TenantNotFound, "A valid owning tenant is required."));
        }

        var entity = new ApplicationEntity
        {
            ApplicationId = request.ApplicationId,
            Name = request.Name,
            Description = request.Description,
            TenantRefId = tenantRefId.Value,
            OwnerTeam = request.OwnerTeam,
            BusinessOwner = request.BusinessOwner,
            TechnicalOwner = request.TechnicalOwner,
            RiskLevel = request.RiskLevel,
            Status = GovernanceStatus.Active,
            SourceOfTruthMode = request.SourceOfTruthMode,
            PolicyCombiningAlgorithm = request.PolicyCombiningAlgorithm,
            CreatedBy = Actor,
        };

        DbContext.Applications.Add(entity);
        object created = new { entity.ApplicationId, entity.Name, entity.Description, TenantId = tenantId, entity.OwnerTeam, entity.BusinessOwner, entity.TechnicalOwner, entity.RiskLevel, entity.Status, entity.SourceOfTruthMode, entity.PolicyCombiningAlgorithm };
        await SaveGovernanceMutationAsync(AuditEventTypes.ApplicationCreated, entity.ApplicationId, null, created, cancellationToken);
        return Created($"/v1/admin/applications/{entity.ApplicationId}", ApplicationResponse.From(entity, tenantId));
    }

    [HttpPut("applications/{applicationId}")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManageApplication)]
    public async Task<IActionResult> UpdateApplicationAsync(string applicationId, [FromBody] UpdateApplicationRequest request, CancellationToken cancellationToken)
    {
        ApplicationEntity? entity = await DbContext.Applications.FirstOrDefaultAsync(application => application.ApplicationId == applicationId, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "Application was not found."));
        }

        if (RequireText(request.Name, "Name") is { } nameError)
        {
            return nameError;
        }

        string tenantId = (request.TenantId ?? string.Empty).Trim();
        Guid? tenantRefId = await DbContext.Tenants.Where(tenant => tenant.TenantId == tenantId).Select(tenant => (Guid?)tenant.Id).FirstOrDefaultAsync(cancellationToken);
        if (tenantRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.TenantNotFound, "A valid owning tenant is required."));
        }

        string? oldTenantId = await DbContext.Tenants.Where(tenant => tenant.Id == entity.TenantRefId).Select(tenant => tenant.TenantId).FirstOrDefaultAsync(cancellationToken);
        object oldValue = new { entity.Name, entity.Description, TenantId = oldTenantId, entity.OwnerTeam, entity.BusinessOwner, entity.TechnicalOwner, entity.RiskLevel, entity.SourceOfTruthMode, entity.PolicyCombiningAlgorithm };
        entity.Name = request.Name;
        entity.Description = request.Description;
        entity.TenantRefId = tenantRefId.Value;
        entity.OwnerTeam = request.OwnerTeam;
        entity.BusinessOwner = request.BusinessOwner;
        entity.TechnicalOwner = request.TechnicalOwner;
        entity.RiskLevel = request.RiskLevel;
        entity.SourceOfTruthMode = request.SourceOfTruthMode;
        entity.PolicyCombiningAlgorithm = request.PolicyCombiningAlgorithm;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedBy = Actor;
        object newValue = new { entity.Name, entity.Description, TenantId = tenantId, entity.OwnerTeam, entity.BusinessOwner, entity.TechnicalOwner, entity.RiskLevel, entity.SourceOfTruthMode, entity.PolicyCombiningAlgorithm };
        await SaveGovernanceMutationAsync(AuditEventTypes.ApplicationUpdated, applicationId, null, oldValue, newValue, cancellationToken);
        return Ok(ApplicationResponse.From(entity, tenantId));
    }

    [HttpPost("applications/{applicationId}/activate")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManageApplication)]
    public async Task<IActionResult> ActivateApplicationAsync(string applicationId, CancellationToken cancellationToken)
    {
        return await SetApplicationStatusAsync(applicationId, GovernanceStatus.Active, AuditEventTypes.ApplicationActivated, cancellationToken);
    }

    [HttpPost("applications/{applicationId}/disable")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManageApplication)]
    public async Task<IActionResult> DisableApplicationAsync(string applicationId, CancellationToken cancellationToken)
    {
        return await SetApplicationStatusAsync(applicationId, GovernanceStatus.Disabled, AuditEventTypes.ApplicationDisabled, cancellationToken);
    }

    [HttpPost("applications/{applicationId}/archive")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManageApplication)]
    public async Task<IActionResult> ArchiveApplicationAsync(string applicationId, CancellationToken cancellationToken)
    {
        return await SetApplicationStatusAsync(applicationId, GovernanceStatus.Archived, AuditEventTypes.ApplicationArchived, cancellationToken);
    }

    [HttpGet("applications/{applicationId}/overview")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ReadOnlyView)]
    public async Task<ActionResult<ApplicationOverviewResponse>> GetApplicationOverviewAsync(string applicationId, CancellationToken cancellationToken)
    {
        ApplicationEntity? app = await DbContext.Applications.AsNoTracking().FirstOrDefaultAsync(a => a.ApplicationId == applicationId, cancellationToken);
        if (app is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "Application was not found."));
        }

        List<RoleEntity> roles = await DbContext.Roles.AsNoTracking().Where(r => r.ApplicationRefId == app.Id).ToListAsync(cancellationToken);
        int permissionCount = await DbContext.Permissions.AsNoTracking().CountAsync(p => p.ApplicationRefId == app.Id, cancellationToken);
        List<PolicyEntity> policies = await DbContext.Policies.AsNoTracking().Where(p => p.ApplicationRefId == app.Id).ToListAsync(cancellationToken);
        var assignmentRows = await DbContext.Assignments.AsNoTracking()
            .Where(x => x.ApplicationRefId == app.Id)
            .Select(x => new { x.State, x.ValidUntil, x.RevokedAt })
            .ToListAsync(cancellationToken);

        Dictionary<string, int> roleRisk = roles
            .GroupBy(r => string.IsNullOrWhiteSpace(r.RiskLevel) ? RiskLevel.Medium : r.RiskLevel.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.Count());

        List<AuditEventEntity> recentAudit = await DbContext.AuditEvents.AsNoTracking()
            .Where(e => e.ApplicationId == applicationId)
            .OrderByDescending(e => e.Timestamp)
            .Take(10)
            .ToListAsync(cancellationToken);

        return new ApplicationOverviewResponse(
            app.ApplicationId,
            app.Name,
            roles.Count,
            roles.Count(r => r.Privileged),
            permissionCount,
            policies.Count,
            policies.Count(p => string.Equals(p.State, WorkflowState.Published, StringComparison.OrdinalIgnoreCase)),
            policies.Count(p => string.Equals(p.State, WorkflowState.Draft, StringComparison.OrdinalIgnoreCase)),
            assignmentRows.Count,
            assignmentRows.Count(r => AssignmentDisplayStatus.Resolve(r.State, r.ValidUntil, r.RevokedAt) == AssignmentDisplayStatus.Active),
            roleRisk,
            recentAudit.Select(AuditEventResponse.From).ToList());
    }

    private async Task<IActionResult> SetApplicationStatusAsync(string applicationId, string status, string eventType, CancellationToken cancellationToken)
    {
        ApplicationEntity? entity = await DbContext.Applications.FirstOrDefaultAsync(application => application.ApplicationId == applicationId, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "Application was not found."));
        }

        object oldValue = new { entity.Status };
        entity.Status = status;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedBy = Actor;
        await SaveGovernanceMutationAsync(eventType, applicationId, null, oldValue, new { entity.Status }, cancellationToken);
        string? statusTenantId = await DbContext.Tenants.AsNoTracking()
            .Where(tenant => tenant.Id == entity.TenantRefId)
            .Select(tenant => tenant.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        return Ok(ApplicationResponse.From(entity, statusTenantId));
    }
}
