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
public sealed class RolePermissionsController : GovernanceControllerBase
{
    public RolePermissionsController(AuthorizationDbContext dbContext)
        : base(dbContext)
    {
    }

    [HttpGet("applications/{applicationId}/role-permissions")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ReadOnlyView)]
    public async Task<IReadOnlyList<RolePermissionResponse>> GetRolePermissionsAsync(string applicationId, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return [];
        }

        Dictionary<Guid, string> roleKeyById = await DbContext.Roles
            .AsNoTracking()
            .Where(entity => entity.ApplicationRefId == applicationRefId.Value)
            .ToDictionaryAsync(entity => entity.Id, entity => entity.RoleKey, cancellationToken);

        Dictionary<Guid, string> permissionKeyById = await DbContext.Permissions
            .AsNoTracking()
            .Where(entity => entity.ApplicationRefId == applicationRefId.Value)
            .ToDictionaryAsync(entity => entity.Id, entity => entity.PermissionKey, cancellationToken);

        List<RolePermissionEntity> mappings = await DbContext.RolePermissions
            .AsNoTracking()
            .Where(entity => entity.ApplicationRefId == applicationRefId.Value)
            .ToListAsync(cancellationToken);

        return mappings
            .Where(entity => roleKeyById.ContainsKey(entity.RoleRefId) && permissionKeyById.ContainsKey(entity.PermissionRefId))
            .Select(entity => RolePermissionResponse.From(entity, roleKeyById[entity.RoleRefId], permissionKeyById[entity.PermissionRefId]))
            .OrderBy(response => response.RoleKey).ThenBy(response => response.PermissionKey)
            .ToList();
    }

    [HttpPost("applications/{applicationId}/role-permissions")]
    [Authorize(Policy = DelegatedAdminPolicyNames.MapRolePermission)]
    public async Task<IActionResult> CreateRolePermissionAsync(string applicationId, [FromBody] CreateRolePermissionRequest request, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "Application was not found."));
        }

        Guid? roleRefId = await DbContext.Roles
            .Where(entity => entity.ApplicationRefId == applicationRefId.Value && entity.RoleKey == request.RoleKey)
            .Select(entity => (Guid?)entity.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (roleRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.RoleNotFound, "Role was not found."));
        }

        Guid? permissionRefId = await DbContext.Permissions
            .Where(entity => entity.ApplicationRefId == applicationRefId.Value && entity.PermissionKey == request.PermissionKey)
            .Select(entity => (Guid?)entity.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (permissionRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.PermissionNotFound, "Permission was not found."));
        }

        var entity = new RolePermissionEntity
        {
            ApplicationRefId = applicationRefId.Value,
            RoleRefId = roleRefId.Value,
            PermissionRefId = permissionRefId.Value,
            State = request.Publish ? WorkflowState.Published : WorkflowState.Draft,
            ValidFrom = DateTimeOffset.UtcNow,
            PublishedAt = request.Publish ? DateTimeOffset.UtcNow : null,
            CreatedBy = Actor,
        };

        DbContext.RolePermissions.Add(entity);
        object created = new { RoleKey = request.RoleKey, PermissionKey = request.PermissionKey, entity.State };
        await SaveGovernanceMutationAsync(AuditEventTypes.RolePermissionCreated, applicationId, null, created, cancellationToken);
        return Created($"/v1/admin/applications/{applicationId}/role-permissions/{entity.Id}", RolePermissionResponse.From(entity, request.RoleKey, request.PermissionKey));
    }

    [HttpPost("applications/{applicationId}/role-permissions/{rolePermissionId:guid}/publish")]
    [Authorize(Policy = DelegatedAdminPolicyNames.MapRolePermission)]
    public async Task<IActionResult> PublishRolePermissionAsync(string applicationId, Guid rolePermissionId, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        RolePermissionEntity? entity = applicationRefId is null
            ? null
            : await DbContext.RolePermissions
                .FirstOrDefaultAsync(rp => rp.ApplicationRefId == applicationRefId.Value && rp.Id == rolePermissionId, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.RolePermissionNotFound, "Role-permission mapping was not found."));
        }

        string roleKey = await RoleKeyByIdAsync(entity.RoleRefId, cancellationToken);
        string permissionKey = await PermissionKeyByIdAsync(entity.PermissionRefId, cancellationToken);

        if (entity.State == WorkflowState.Published)
        {
            return Ok(RolePermissionResponse.From(entity, roleKey, permissionKey));
        }

        object oldValue = new { entity.State };
        entity.State = WorkflowState.Published;
        entity.PublishedAt = DateTimeOffset.UtcNow;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedBy = Actor;
        await SaveGovernanceMutationAsync(AuditEventTypes.RolePermissionPublished, applicationId, null, oldValue, new { RoleKey = roleKey, PermissionKey = permissionKey, entity.State }, cancellationToken);
        return Ok(RolePermissionResponse.From(entity, roleKey, permissionKey));
    }

    [HttpDelete("applications/{applicationId}/role-permissions/{rolePermissionId:guid}")]
    [Authorize(Policy = DelegatedAdminPolicyNames.MapRolePermission)]
    public async Task<IActionResult> DeleteRolePermissionAsync(string applicationId, Guid rolePermissionId, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        RolePermissionEntity? entity = applicationRefId is null
            ? null
            : await DbContext.RolePermissions
                .FirstOrDefaultAsync(rp => rp.ApplicationRefId == applicationRefId.Value && rp.Id == rolePermissionId, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.RolePermissionNotFound, "Role-permission mapping was not found."));
        }

        string revokedRoleKey = await RoleKeyByIdAsync(entity.RoleRefId, cancellationToken);
        string revokedPermissionKey = await PermissionKeyByIdAsync(entity.PermissionRefId, cancellationToken);
        object oldValue = new { RoleKey = revokedRoleKey, PermissionKey = revokedPermissionKey, entity.State };
        DbContext.RolePermissions.Remove(entity);
        await SaveGovernanceMutationAsync(AuditEventTypes.RolePermissionRevoked, applicationId, null, oldValue, null, cancellationToken);
        return NoContent();
    }
}
