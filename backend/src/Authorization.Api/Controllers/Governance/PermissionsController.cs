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
public sealed class PermissionsController : GovernanceControllerBase
{
    public PermissionsController(AuthorizationDbContext dbContext)
        : base(dbContext)
    {
    }

    [HttpGet("applications/{applicationId}/permissions")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ReadOnlyView)]
    public async Task<PagedResult<PermissionResponse>> GetPermissionsAsync(string applicationId, [FromQuery] string? q, [FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return new PagedResult<PermissionResponse>([], 1, 0, 0);
        }

        List<PermissionEntity> permissions = await DbContext.Permissions
            .AsNoTracking()
            .Where(entity => entity.ApplicationRefId == applicationRefId.Value)
            .OrderBy(entity => entity.PermissionKey)
            .ToListAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(status))
        {
            permissions = permissions.Where(p => string.Equals(p.Status, status, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            string needle = q.Trim();
            permissions = permissions
                .Where(p => p.PermissionKey.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || p.Resource.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || p.Action.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        PageRequest paging = PageRequest.From(page, pageSize);
        int total = permissions.Count;
        if (paging.Enabled)
        {
            permissions = permissions.Skip(paging.Skip).Take(paging.PageSize).ToList();
        }

        List<PermissionResponse> items = permissions.Select(entity => PermissionResponse.From(entity, applicationId)).ToList();
        return new PagedResult<PermissionResponse>(items, paging.Enabled ? paging.Page : 1, paging.Enabled ? paging.PageSize : total, total);
    }

    [HttpPost("applications/{applicationId}/permissions")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManagePermissions)]
    public async Task<IActionResult> CreatePermissionAsync(string applicationId, [FromBody] CreatePermissionRequest request, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "Application was not found."));
        }

        if (RequireText(request.PermissionKey, "Permission key") is { } permissionKeyError)
        {
            return permissionKeyError;
        }

        if (RequireText(request.Resource, "Resource") is { } resourceError)
        {
            return resourceError;
        }

        if (RequireText(request.Action, "Action") is { } actionError)
        {
            return actionError;
        }

        if (await DbContext.Permissions.AnyAsync(entity => entity.ApplicationRefId == applicationRefId.Value && entity.PermissionKey == request.PermissionKey, cancellationToken))
        {
            return Conflict(Error(GovernanceErrorCodes.PermissionExists, "Permission already exists."));
        }

        var entity = new PermissionEntity
        {
            ApplicationRefId = applicationRefId.Value,
            PermissionKey = request.PermissionKey,
            Resource = request.Resource,
            Action = request.Action,
            Description = request.Description,
            RiskLevel = request.RiskLevel,
            Status = GovernanceStatus.Active,
            CreatedBy = Actor,
        };

        DbContext.Permissions.Add(entity);
        object created = new { entity.PermissionKey, entity.Resource, entity.Action, entity.Description, entity.RiskLevel, entity.Status };
        await SaveGovernanceMutationAsync(AuditEventTypes.PermissionCreated, applicationId, null, created, cancellationToken);
        return Created($"/v1/admin/applications/{applicationId}/permissions/{entity.PermissionKey}", PermissionResponse.From(entity, applicationId));
    }

    [HttpPut("applications/{applicationId}/permissions/{permissionKey}")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManagePermissions)]
    public async Task<IActionResult> UpdatePermissionAsync(string applicationId, string permissionKey, [FromBody] UpdatePermissionRequest request, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        PermissionEntity? entity = applicationRefId is null
            ? null
            : await DbContext.Permissions.FirstOrDefaultAsync(permission => permission.ApplicationRefId == applicationRefId.Value && permission.PermissionKey == permissionKey, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.PermissionNotFound, "Permission was not found."));
        }

        object oldValue = new { entity.Description, entity.RiskLevel };
        entity.Description = request.Description;
        entity.RiskLevel = request.RiskLevel;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedBy = Actor;
        await SaveGovernanceMutationAsync(AuditEventTypes.PermissionUpdated, applicationId, null, oldValue, new { entity.Description, entity.RiskLevel }, cancellationToken);
        return Ok(PermissionResponse.From(entity, applicationId));
    }

    [HttpDelete("applications/{applicationId}/permissions/{permissionKey}")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManagePermissions)]
    public async Task<IActionResult> DeletePermissionAsync(string applicationId, string permissionKey, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        PermissionEntity? entity = applicationRefId is null
            ? null
            : await DbContext.Permissions.FirstOrDefaultAsync(permission => permission.ApplicationRefId == applicationRefId.Value && permission.PermissionKey == permissionKey, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.PermissionNotFound, "Permission was not found."));
        }

        bool hasMappings = await DbContext.RolePermissions.AnyAsync(m => m.PermissionRefId == entity.Id, cancellationToken);
        if (hasMappings)
        {
            return Conflict(Error(GovernanceErrorCodes.PermissionInUse, "This permission is still granted to one or more roles. Remove those grants before deleting."));
        }

        bool hasPolicies = await DbContext.Policies.AnyAsync(p => p.PermissionRefId == entity.Id, cancellationToken);
        if (hasPolicies)
        {
            return Conflict(Error(GovernanceErrorCodes.PermissionInUse, "This permission is referenced by one or more policies. Delete those policies before deleting."));
        }

        DbContext.Permissions.Remove(entity);
        await SaveGovernanceMutationAsync(AuditEventTypes.PermissionDeleted, applicationId, null, new { entity.PermissionKey, entity.Resource, entity.Action }, newValue: null, cancellationToken);
        return NoContent();
    }

    [HttpPost("applications/{applicationId}/permissions/{permissionKey}/activate")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManagePermissions)]
    public async Task<IActionResult> ActivatePermissionAsync(string applicationId, string permissionKey, CancellationToken cancellationToken)
    {
        return await SetPermissionStatusAsync(applicationId, permissionKey, GovernanceStatus.Active, AuditEventTypes.PermissionActivated, cancellationToken);
    }

    [HttpPost("applications/{applicationId}/permissions/{permissionKey}/disable")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManagePermissions)]
    public async Task<IActionResult> DisablePermissionAsync(string applicationId, string permissionKey, CancellationToken cancellationToken)
    {
        return await SetPermissionStatusAsync(applicationId, permissionKey, GovernanceStatus.Disabled, AuditEventTypes.PermissionDisabled, cancellationToken);
    }

    [HttpPost("applications/{applicationId}/permissions/{permissionKey}/archive")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManagePermissions)]
    public async Task<IActionResult> ArchivePermissionAsync(string applicationId, string permissionKey, CancellationToken cancellationToken)
    {
        return await SetPermissionStatusAsync(applicationId, permissionKey, GovernanceStatus.Archived, AuditEventTypes.PermissionArchived, cancellationToken);
    }

    private async Task<IActionResult> SetPermissionStatusAsync(string applicationId, string permissionKey, string status, string eventType, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        PermissionEntity? entity = applicationRefId is null
            ? null
            : await DbContext.Permissions.FirstOrDefaultAsync(permission => permission.ApplicationRefId == applicationRefId.Value && permission.PermissionKey == permissionKey, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.PermissionNotFound, "Permission was not found."));
        }

        if (string.Equals(entity.Status, status, StringComparison.Ordinal))
        {
            return Ok(PermissionResponse.From(entity, applicationId));
        }

        object oldValue = new { entity.Status };
        entity.Status = status;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedBy = Actor;
        await SaveGovernanceMutationAsync(eventType, applicationId, null, oldValue, new { entity.Status }, cancellationToken);
        return Ok(PermissionResponse.From(entity, applicationId));
    }
}
