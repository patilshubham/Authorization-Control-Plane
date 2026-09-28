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
public sealed class RolesController : GovernanceControllerBase
{
    public RolesController(AuthorizationDbContext dbContext)
        : base(dbContext)
    {
    }

    [HttpGet("applications/{applicationId}/roles")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ReadOnlyView)]
    public async Task<PagedResult<RoleWithPermissionsResponse>> GetRolesAsync(string applicationId, [FromQuery] string? q, [FromQuery] string? status, [FromQuery] bool? privileged, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return new PagedResult<RoleWithPermissionsResponse>([], 1, 0, 0);
        }

        List<RoleEntity> roles = await DbContext.Roles
            .AsNoTracking()
            .Where(entity => entity.ApplicationRefId == applicationRefId.Value)
            .OrderBy(entity => entity.RoleKey)
            .ToListAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(status))
        {
            roles = roles.Where(r => string.Equals(r.Status, status, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (privileged is bool wantPrivileged)
        {
            roles = roles.Where(r => r.Privileged == wantPrivileged).ToList();
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            string needle = q.Trim();
            roles = roles
                .Where(r => r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || r.RoleKey.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        PageRequest paging = PageRequest.From(page, pageSize);
        int total = roles.Count;
        if (paging.Enabled)
        {
            roles = roles.Skip(paging.Skip).Take(paging.PageSize).ToList();
        }

        List<RolePermissionEntity> mappings = await DbContext.RolePermissions
            .AsNoTracking()
            .Where(entity => entity.ApplicationRefId == applicationRefId.Value && entity.State == WorkflowState.Published)
            .ToListAsync(cancellationToken);

        Dictionary<Guid, string> permissionKeyById = await DbContext.Permissions
            .AsNoTracking()
            .Where(entity => entity.ApplicationRefId == applicationRefId.Value)
            .ToDictionaryAsync(entity => entity.Id, entity => entity.PermissionKey, cancellationToken);

        List<RoleWithPermissionsResponse> items = roles.Select(role => new RoleWithPermissionsResponse(
            role.RoleKey,
            role.Name,
            role.Privileged,
            role.RiskLevel ?? RiskLevel.Medium,
            role.Status,
            mappings.Where(m => m.RoleRefId == role.Id && permissionKeyById.ContainsKey(m.PermissionRefId)).Select(m => permissionKeyById[m.PermissionRefId]).ToList(),
            role.Description)).ToList();
        return new PagedResult<RoleWithPermissionsResponse>(items, paging.Enabled ? paging.Page : 1, paging.Enabled ? paging.PageSize : total, total);
    }

    [HttpPost("applications/{applicationId}/roles")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManageRoles)]
    public async Task<IActionResult> CreateRoleAsync(string applicationId, [FromBody] CreateRoleRequest request, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "Application was not found."));
        }

        if (RequireText(request.RoleKey, "Role key") is { } roleKeyError)
        {
            return roleKeyError;
        }

        if (RequireText(request.Name, "Name") is { } nameError)
        {
            return nameError;
        }

        if (await DbContext.Roles.AnyAsync(entity => entity.ApplicationRefId == applicationRefId.Value && entity.RoleKey == request.RoleKey, cancellationToken))
        {
            return Conflict(Error(GovernanceErrorCodes.RoleExists, "Role already exists."));
        }

        var entity = new RoleEntity
        {
            ApplicationRefId = applicationRefId.Value,
            RoleKey = request.RoleKey,
            Name = request.Name,
            Description = request.Description,
            Privileged = request.Privileged,
            RiskLevel = request.RiskLevel,
            Status = GovernanceStatus.Active,
            CreatedBy = Actor,
        };

        DbContext.Roles.Add(entity);
        object created = new { entity.RoleKey, entity.Name, entity.Description, entity.Privileged, entity.RiskLevel, entity.Status };
        await SaveGovernanceMutationAsync(AuditEventTypes.RoleCreated, applicationId, null, created, cancellationToken);
        return Created($"/v1/admin/applications/{applicationId}/roles/{entity.RoleKey}", RoleResponse.From(entity, applicationId));
    }

    [HttpPut("applications/{applicationId}/roles/{roleKey}")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManageRoles)]
    public async Task<IActionResult> UpdateRoleAsync(string applicationId, string roleKey, [FromBody] UpdateRoleRequest request, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        RoleEntity? entity = applicationRefId is null
            ? null
            : await DbContext.Roles.FirstOrDefaultAsync(role => role.ApplicationRefId == applicationRefId.Value && role.RoleKey == roleKey, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.RoleNotFound, "Role was not found."));
        }

        if (RequireText(request.Name, "Name") is { } nameError)
        {
            return nameError;
        }

        object oldValue = new { entity.Name, entity.Description, entity.Privileged, entity.RiskLevel };
        entity.Name = request.Name;
        entity.Description = request.Description;
        entity.Privileged = request.Privileged;
        entity.RiskLevel = request.RiskLevel;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedBy = Actor;
        await SaveGovernanceMutationAsync(AuditEventTypes.RoleUpdated, applicationId, null, oldValue, new { entity.Name, entity.Description, entity.Privileged, entity.RiskLevel }, cancellationToken);
        return Ok(RoleResponse.From(entity, applicationId));
    }

    [HttpDelete("applications/{applicationId}/roles/{roleKey}")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManageRoles)]
    public async Task<IActionResult> DeleteRoleAsync(string applicationId, string roleKey, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        RoleEntity? entity = applicationRefId is null
            ? null
            : await DbContext.Roles.FirstOrDefaultAsync(role => role.ApplicationRefId == applicationRefId.Value && role.RoleKey == roleKey, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.RoleNotFound, "Role was not found."));
        }

        bool hasAssignments = await DbContext.Assignments.AnyAsync(a => a.RoleRefId == entity.Id, cancellationToken);
        if (hasAssignments)
        {
            return Conflict(Error(GovernanceErrorCodes.RoleInUse, "This role still has assignments (including historical). Revoke and remove them, or archive the role instead."));
        }

        bool hasMappings = await DbContext.RolePermissions.AnyAsync(m => m.RoleRefId == entity.Id, cancellationToken);
        if (hasMappings)
        {
            return Conflict(Error(GovernanceErrorCodes.RoleInUse, "This role still grants permissions. Remove its permission grants before deleting."));
        }

        DbContext.Roles.Remove(entity);
        await SaveGovernanceMutationAsync(AuditEventTypes.RoleDeleted, applicationId, null, new { entity.RoleKey, entity.Name }, newValue: null, cancellationToken);
        return NoContent();
    }

    [HttpPost("applications/{applicationId}/roles/{roleKey}/activate")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManageRoles)]
    public async Task<IActionResult> ActivateRoleAsync(string applicationId, string roleKey, CancellationToken cancellationToken)
    {
        return await SetRoleStatusAsync(applicationId, roleKey, GovernanceStatus.Active, AuditEventTypes.RoleActivated, cancellationToken);
    }

    [HttpPost("applications/{applicationId}/roles/{roleKey}/disable")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManageRoles)]
    public async Task<IActionResult> DisableRoleAsync(string applicationId, string roleKey, CancellationToken cancellationToken)
    {
        return await SetRoleStatusAsync(applicationId, roleKey, GovernanceStatus.Disabled, AuditEventTypes.RoleDisabled, cancellationToken);
    }

    [HttpPost("applications/{applicationId}/roles/{roleKey}/archive")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManageRoles)]
    public async Task<IActionResult> ArchiveRoleAsync(string applicationId, string roleKey, CancellationToken cancellationToken)
    {
        return await SetRoleStatusAsync(applicationId, roleKey, GovernanceStatus.Archived, AuditEventTypes.RoleArchived, cancellationToken);
    }

    private async Task<IActionResult> SetRoleStatusAsync(string applicationId, string roleKey, string status, string eventType, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        RoleEntity? entity = applicationRefId is null
            ? null
            : await DbContext.Roles.FirstOrDefaultAsync(role => role.ApplicationRefId == applicationRefId.Value && role.RoleKey == roleKey, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.RoleNotFound, "Role was not found."));
        }

        if (string.Equals(entity.Status, status, StringComparison.Ordinal))
        {
            return Ok(RoleResponse.From(entity, applicationId));
        }

        object oldValue = new { entity.Status };
        entity.Status = status;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedBy = Actor;
        await SaveGovernanceMutationAsync(eventType, applicationId, null, oldValue, new { entity.Status }, cancellationToken);
        return Ok(RoleResponse.From(entity, applicationId));
    }
}
