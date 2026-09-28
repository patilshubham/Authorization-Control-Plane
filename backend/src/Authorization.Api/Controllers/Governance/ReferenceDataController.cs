using Authorization.Api.Authorization;
using Authorization.Api.Constants;
using Authorization.Api.Contracts;
using Authorization.Api.Governance;
using Authorization.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Controllers;

/// <summary>
/// Delegated-admin CRUD for application-scoped reference-data documents. These named JSON documents
/// (e.g. an allow-list of country codes) are referenced by policy conditions via
/// <c>reference.&lt;key&gt;</c>, letting admins maintain shared lookup values in one place instead of
/// duplicating literals across policies. Deletes are soft (status flips to ARCHIVED) so the runtime
/// engine — which only reads ACTIVE rows — stops resolving them while history is preserved.
/// </summary>
[ApiController]
[Authorize(Policy = DelegatedAdminPolicyNames.AdminApi)]
[Route("v1/admin")]
public sealed class ReferenceDataController : GovernanceControllerBase
{
    public ReferenceDataController(AuthorizationDbContext dbContext)
        : base(dbContext)
    {
    }

    [HttpGet("applications/{applicationId}/reference-data")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ReadOnlyView)]
    public async Task<PagedResult<ReferenceDataResponse>> GetReferenceDataAsync(string applicationId, [FromQuery] string? q, [FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return new PagedResult<ReferenceDataResponse>([], 1, 0, 0);
        }

        List<ReferenceDataEntity> rows = await DbContext.ReferenceData
            .AsNoTracking()
            .Where(entity => entity.ApplicationRefId == applicationRefId.Value)
            .OrderBy(entity => entity.Key)
            .ToListAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(status))
        {
            rows = rows.Where(entity => string.Equals(entity.Status, status, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            string needle = q.Trim();
            rows = rows
                .Where(entity => entity.Key.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || (entity.Description ?? string.Empty).Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        PageRequest paging = PageRequest.From(page, pageSize);
        int total = rows.Count;
        if (paging.Enabled)
        {
            rows = rows.Skip(paging.Skip).Take(paging.PageSize).ToList();
        }

        List<ReferenceDataResponse> items = rows.Select(ReferenceDataResponse.From).ToList();
        return new PagedResult<ReferenceDataResponse>(items, paging.Enabled ? paging.Page : 1, paging.Enabled ? paging.PageSize : total, total);
    }

    [HttpPost("applications/{applicationId}/reference-data")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManagePolicies)]
    public async Task<IActionResult> CreateReferenceDataAsync(string applicationId, [FromBody] CreateReferenceDataRequest request, CancellationToken cancellationToken)
    {
        string? valueError = ReferenceDataValueValidator.Validate(request.Value);
        if (valueError is not null)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ReferenceDataValueInvalid, valueError));
        }

        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "Application was not found."));
        }

        string key = request.Key.Trim();
        bool exists = await DbContext.ReferenceData
            .AnyAsync(entity => entity.ApplicationRefId == applicationRefId.Value && entity.Key == key, cancellationToken);
        if (exists)
        {
            return Conflict(Error(GovernanceErrorCodes.ReferenceDataExists, "Reference data with this key already exists."));
        }

        var entity = new ReferenceDataEntity
        {
            ApplicationRefId = applicationRefId.Value,
            Key = key,
            Description = request.Description,
            Value = request.Value,
            Status = GovernanceStatus.Active,
            CreatedBy = Actor,
        };

        DbContext.ReferenceData.Add(entity);
        object created = new { entity.Key, entity.Description, entity.Value, entity.Status };
        await SaveGovernanceMutationAsync(AuditEventTypes.ReferenceDataCreated, applicationId, null, created, cancellationToken);
        return Created($"/v1/admin/applications/{applicationId}/reference-data/{entity.Key}", ReferenceDataResponse.From(entity));
    }

    [HttpPut("applications/{applicationId}/reference-data/{key}")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManagePolicies)]
    public async Task<IActionResult> UpdateReferenceDataAsync(string applicationId, string key, [FromBody] UpdateReferenceDataRequest request, CancellationToken cancellationToken)
    {
        string? valueError = ReferenceDataValueValidator.Validate(request.Value);
        if (valueError is not null)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ReferenceDataValueInvalid, valueError));
        }

        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        ReferenceDataEntity? entity = applicationRefId is null
            ? null
            : await DbContext.ReferenceData.FirstOrDefaultAsync(row => row.ApplicationRefId == applicationRefId.Value && row.Key == key, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ReferenceDataNotFound, "Reference data was not found."));
        }

        object oldValue = new { entity.Description, entity.Value, entity.Status };
        entity.Description = request.Description;
        entity.Value = request.Value;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedBy = Actor;
        await SaveGovernanceMutationAsync(AuditEventTypes.ReferenceDataUpdated, applicationId, null, oldValue, new { entity.Key, entity.Description, entity.Value, entity.Status }, cancellationToken);
        return Ok(ReferenceDataResponse.From(entity));
    }

    [HttpDelete("applications/{applicationId}/reference-data/{key}")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManagePolicies)]
    public async Task<IActionResult> DeleteReferenceDataAsync(string applicationId, string key, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        ReferenceDataEntity? entity = applicationRefId is null
            ? null
            : await DbContext.ReferenceData.FirstOrDefaultAsync(row => row.ApplicationRefId == applicationRefId.Value && row.Key == key, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ReferenceDataNotFound, "Reference data was not found."));
        }

        object oldValue = new { entity.Key, entity.Status };
        entity.Status = GovernanceStatus.Archived;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedBy = Actor;
        await SaveGovernanceMutationAsync(AuditEventTypes.ReferenceDataDeleted, applicationId, null, oldValue, new { entity.Key, entity.Status }, cancellationToken);
        return NoContent();
    }
}
