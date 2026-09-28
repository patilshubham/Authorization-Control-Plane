using System.Text.Json;
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
public sealed class PoliciesController : GovernanceControllerBase
{
    public PoliciesController(AuthorizationDbContext dbContext)
        : base(dbContext)
    {
    }

    [HttpGet("applications/{applicationId}/policies")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ReadOnlyView)]
    public async Task<PagedResult<PolicyResponse>> GetPoliciesAsync(string applicationId, [FromQuery] string? q, [FromQuery] string? effect, [FromQuery] string? state, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return new PagedResult<PolicyResponse>([], 1, 0, 0);
        }

        Dictionary<Guid, string> permissionKeyById = await DbContext.Permissions
            .AsNoTracking()
            .Where(entity => entity.ApplicationRefId == applicationRefId.Value)
            .ToDictionaryAsync(entity => entity.Id, entity => entity.PermissionKey, cancellationToken);

        List<PolicyEntity> policies = await DbContext.Policies
            .AsNoTracking()
            .Where(entity => entity.ApplicationRefId == applicationRefId.Value)
            .OrderBy(entity => entity.PolicyKey)
            .ToListAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(effect))
        {
            policies = policies.Where(p => string.Equals(p.Effect, effect, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(state))
        {
            policies = policies.Where(p => string.Equals(p.State, state, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            string needle = q.Trim();
            policies = policies
                .Where(p => p.PolicyKey.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || permissionKeyById.GetValueOrDefault(p.PermissionRefId, string.Empty).Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        PageRequest paging = PageRequest.From(page, pageSize);
        int total = policies.Count;
        if (paging.Enabled)
        {
            policies = policies.Skip(paging.Skip).Take(paging.PageSize).ToList();
        }

        List<PolicyResponse> items = policies
            .Select(entity => PolicyResponse.From(entity, permissionKeyById.GetValueOrDefault(entity.PermissionRefId, string.Empty)))
            .ToList();
        return new PagedResult<PolicyResponse>(items, paging.Enabled ? paging.Page : 1, paging.Enabled ? paging.PageSize : total, total);
    }

    [HttpPost("applications/{applicationId}/policies")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManagePolicies)]
    public async Task<IActionResult> CreatePolicyAsync(string applicationId, [FromBody] CreatePolicyRequest request, CancellationToken cancellationToken)
    {
        string? conditionError = PolicyConditionValidator.Validate(request.Conditions);
        if (conditionError is not null)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.PolicyConditionsInvalid, conditionError));
        }

        string? obligationsError = PolicyObligationsValidator.Validate(request.Obligations);
        if (obligationsError is not null)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.PolicyObligationsInvalid, obligationsError));
        }

        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "Application was not found."));
        }

        Guid? permissionRefId = await DbContext.Permissions
            .Where(entity => entity.ApplicationRefId == applicationRefId.Value && entity.PermissionKey == request.PermissionKey)
            .Select(entity => (Guid?)entity.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (permissionRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.PermissionNotFound, "Permission was not found."));
        }

        var entity = new PolicyEntity
        {
            ApplicationRefId = applicationRefId.Value,
            PolicyKey = request.PolicyKey,
            PermissionRefId = permissionRefId.Value,
            Effect = request.Effect,
            Conditions = request.Conditions,
            Priority = request.Priority,
            Obligations = string.IsNullOrWhiteSpace(request.Obligations) ? "[]" : request.Obligations,
            State = request.Publish ? WorkflowState.Published : WorkflowState.Draft,
            PublishedAt = request.Publish ? DateTimeOffset.UtcNow : null,
            CreatedBy = Actor,
        };

        DbContext.Policies.Add(entity);
        object created = new { entity.PolicyKey, PermissionKey = request.PermissionKey, entity.Effect, entity.Conditions, entity.Priority, entity.Obligations, entity.State };
        await SaveGovernanceMutationAsync(AuditEventTypes.PolicyCreated, applicationId, null, created, cancellationToken);
        return Created($"/v1/admin/applications/{applicationId}/policies/{entity.PolicyKey}", PolicyResponse.From(entity, request.PermissionKey));
    }

    [HttpPost("applications/{applicationId}/policies/{policyKey}/publish")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManagePolicies)]
    public async Task<IActionResult> PublishPolicyAsync(string applicationId, string policyKey, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        PolicyEntity? entity = applicationRefId is null
            ? null
            : await DbContext.Policies.FirstOrDefaultAsync(policy => policy.ApplicationRefId == applicationRefId.Value && policy.PolicyKey == policyKey, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.PolicyNotFound, "Policy was not found."));
        }

        object oldPolicyValue = new { entity.State };
        entity.State = WorkflowState.Published;
        entity.PublishedAt = DateTimeOffset.UtcNow;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedBy = Actor;
        await SaveGovernanceMutationAsync(AuditEventTypes.PolicyPublished, applicationId, null, oldPolicyValue, new { entity.PolicyKey, entity.State }, cancellationToken);
        string publishedPermissionKey = await PermissionKeyByIdAsync(entity.PermissionRefId, cancellationToken);
        return Ok(PolicyResponse.From(entity, publishedPermissionKey));
    }

    [HttpPut("applications/{applicationId}/policies/{policyKey}")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManagePolicies)]
    public async Task<IActionResult> UpdatePolicyAsync(string applicationId, string policyKey, [FromBody] UpdatePolicyRequest request, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        PolicyEntity? entity = applicationRefId is null
            ? null
            : await DbContext.Policies.FirstOrDefaultAsync(policy => policy.ApplicationRefId == applicationRefId.Value && policy.PolicyKey == policyKey, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.PolicyNotFound, "Policy was not found."));
        }

        if (!string.Equals(entity.State, WorkflowState.Draft, StringComparison.OrdinalIgnoreCase))
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.PolicyNotEditable, "Only draft policies can be edited."));
        }

        string? conditionError = PolicyConditionValidator.Validate(request.Conditions);
        if (conditionError is not null)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.PolicyConditionsInvalid, conditionError));
        }

        string? obligationsError = PolicyObligationsValidator.Validate(request.Obligations);
        if (obligationsError is not null)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.PolicyObligationsInvalid, obligationsError));
        }

        object oldPolicyValue = new { entity.Effect, entity.Conditions, entity.Priority, entity.Obligations };
        entity.Effect = request.Effect;
        entity.Conditions = request.Conditions;
        entity.Priority = request.Priority;
        entity.Obligations = string.IsNullOrWhiteSpace(request.Obligations) ? "[]" : request.Obligations;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedBy = Actor;
        if (request.Publish)
        {
            entity.State = WorkflowState.Published;
            entity.PublishedAt = DateTimeOffset.UtcNow;
        }

        await SaveGovernanceMutationAsync(AuditEventTypes.PolicyUpdated, applicationId, null, oldPolicyValue, new { entity.PolicyKey, entity.Effect, entity.Priority, entity.State }, cancellationToken);
        string updatedPermissionKey = await PermissionKeyByIdAsync(entity.PermissionRefId, cancellationToken);
        return Ok(PolicyResponse.From(entity, updatedPermissionKey));
    }

    [HttpDelete("applications/{applicationId}/policies/{policyKey}")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ManagePolicies)]
    public async Task<IActionResult> DeletePolicyAsync(string applicationId, string policyKey, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        PolicyEntity? entity = applicationRefId is null
            ? null
            : await DbContext.Policies.FirstOrDefaultAsync(policy => policy.ApplicationRefId == applicationRefId.Value && policy.PolicyKey == policyKey, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.PolicyNotFound, "Policy was not found."));
        }

        DbContext.Policies.Remove(entity);
        await SaveGovernanceMutationAsync(AuditEventTypes.PolicyDeleted, applicationId, null, new { entity.PolicyKey, entity.Effect, entity.State }, newValue: null, cancellationToken);
        return NoContent();
    }

    [HttpGet("applications/{applicationId}/policies/{policyKey}/history")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ReadOnlyView)]
    public async Task<ActionResult<PolicyHistoryResponse>> GetPolicyHistoryAsync(string applicationId, string policyKey, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "Application was not found."));
        }

        // The change timeline is reconstructed from the append-only audit log. Every policy
        // mutation records the PolicyKey in its old/new value, so we bound the scan to this
        // application's policy events and keep only those that reference this policy.
        List<AuditEventEntity> events = await DbContext.AuditEvents.AsNoTracking()
            .Where(entity => entity.ApplicationId == applicationId
                && entity.EventType != null
                && entity.EventType.StartsWith("POLICY"))
            .OrderByDescending(entity => entity.Timestamp)
            .Take(500)
            .ToListAsync(cancellationToken);

        List<PolicyHistoryEntry> entries = events
            .Where(entity => ReferencesPolicy(entity.NewValue, policyKey) || ReferencesPolicy(entity.OldValue, policyKey))
            .Select(entity => new PolicyHistoryEntry(
                entity.EventType!,
                entity.ActorEmail ?? "system",
                entity.ActorRole,
                entity.Timestamp,
                entity.OldValue,
                entity.NewValue))
            .ToList();

        return Ok(new PolicyHistoryResponse(entries));
    }

    // Whether a serialized audit value object names this policy via a top-level PolicyKey property.
    private static bool ReferencesPolicy(string? json, string policyKey)
    {
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "PolicyKey", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && string.Equals(property.Value.GetString(), policyKey, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // A malformed historical value is simply not matched.
        }

        return false;
    }
}
