using Authorization.Api.Authorization;
using Authorization.Api.Constants;
using Authorization.Api.Governance;
using Authorization.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Controllers;

/// <summary>
/// Scheduled access-review (recertification) campaigns for an application. A campaign is created as
/// a DRAFT, activated to snapshot the in-scope active assignments into review items, reviewed
/// (KEEP / REVOKE / NEEDS_INFO per item), then finalised — which applies the approved REVOKE
/// outcomes through the normal audited assignment-revoke path and closes the campaign.
/// </summary>
[ApiController]
[Authorize(Policy = DelegatedAdminPolicyNames.AdminApi)]
[Route("v1/admin")]
public sealed class ReviewCampaignsController : GovernanceControllerBase
{
    public ReviewCampaignsController(AuthorizationDbContext dbContext)
        : base(dbContext)
    {
    }

    [HttpGet("applications/{applicationId}/review-campaigns")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ReadOnlyView)]
    public async Task<ActionResult<IReadOnlyList<ReviewCampaignResponse>>> ListAsync(string applicationId, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return Ok(Array.Empty<ReviewCampaignResponse>());
        }

        List<ReviewCampaignEntity> campaigns = await DbContext.ReviewCampaigns.AsNoTracking()
            .Where(c => c.ApplicationRefId == applicationRefId.Value)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

        List<Guid> campaignIds = campaigns.Select(c => c.Id).ToList();
        List<ReviewItemEntity> items = await DbContext.ReviewItems.AsNoTracking()
            .Where(i => campaignIds.Contains(i.CampaignRefId))
            .ToListAsync(cancellationToken);
        ILookup<Guid, ReviewItemEntity> itemsByCampaign = items.ToLookup(i => i.CampaignRefId);

        return Ok(campaigns.Select(c => ToSummary(c, itemsByCampaign[c.Id])).ToList());
    }

    [HttpGet("applications/{applicationId}/review-campaigns/{campaignId:guid}")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ReadOnlyView)]
    public async Task<ActionResult<ReviewCampaignDetailResponse>> GetAsync(string applicationId, Guid campaignId, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        ReviewCampaignEntity? campaign = applicationRefId is null
            ? null
            : await DbContext.ReviewCampaigns.AsNoTracking()
                .FirstOrDefaultAsync(c => c.ApplicationRefId == applicationRefId.Value && c.Id == campaignId, cancellationToken);
        if (campaign is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "Campaign was not found."));
        }

        List<ReviewItemEntity> items = await DbContext.ReviewItems.AsNoTracking()
            .Where(i => i.CampaignRefId == campaignId)
            .OrderBy(i => i.SubjectEmail).ThenBy(i => i.RoleKey)
            .ToListAsync(cancellationToken);

        return Ok(new ReviewCampaignDetailResponse(
            ToSummary(campaign, items),
            items.Select(ToItemResponse).ToList()));
    }

    [HttpPost("applications/{applicationId}/review-campaigns")]
    [Authorize(Policy = DelegatedAdminPolicyNames.AssignRoles)]
    public async Task<ActionResult<ReviewCampaignResponse>> CreateAsync(string applicationId, [FromBody] CreateReviewCampaignRequest request, CancellationToken cancellationToken)
    {
        string name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "A campaign name is required."));
        }

        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "Application was not found."));
        }

        var entity = new ReviewCampaignEntity
        {
            ApplicationRefId = applicationRefId.Value,
            Name = name,
            Status = "DRAFT",
            DueAt = request.DueAt,
            CreatedBy = Actor,
        };
        DbContext.ReviewCampaigns.Add(entity);
        await SaveGovernanceMutationAsync(AuditEventTypes.ReviewCampaignCreated, applicationId, null, new { entity.Name, entity.DueAt }, cancellationToken);
        return Ok(ToSummary(entity, []));
    }

    [HttpPost("applications/{applicationId}/review-campaigns/{campaignId:guid}/activate")]
    [Authorize(Policy = DelegatedAdminPolicyNames.AssignRoles)]
    public async Task<ActionResult<ReviewCampaignDetailResponse>> ActivateAsync(string applicationId, Guid campaignId, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        ReviewCampaignEntity? campaign = applicationRefId is null
            ? null
            : await DbContext.ReviewCampaigns.FirstOrDefaultAsync(c => c.ApplicationRefId == applicationRefId.Value && c.Id == campaignId, cancellationToken);
        if (campaign is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "Campaign was not found."));
        }

        if (campaign.Status != "DRAFT")
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "Only a draft campaign can be activated."));
        }

        // Snapshot every currently-active assignment in the application into a review item.
        List<AssignmentEntity> assignments = await DbContext.Assignments.AsNoTracking()
            .Where(a => a.ApplicationRefId == applicationRefId!.Value)
            .ToListAsync(cancellationToken);
        Dictionary<Guid, string> roleKeyById = await DbContext.Roles.AsNoTracking()
            .Where(r => r.ApplicationRefId == applicationRefId!.Value)
            .ToDictionaryAsync(r => r.Id, r => r.RoleKey, cancellationToken);

        int generated = 0;
        foreach (AssignmentEntity assignment in assignments)
        {
            if (AssignmentDisplayStatus.Resolve(assignment.State, assignment.ValidUntil, assignment.RevokedAt) != "ACTIVE")
            {
                continue;
            }

            DbContext.ReviewItems.Add(new ReviewItemEntity
            {
                CampaignRefId = campaign.Id,
                AssignmentRefId = assignment.Id,
                SubjectEmail = assignment.SubjectEmail ?? string.Empty,
                RoleKey = roleKeyById.GetValueOrDefault(assignment.RoleRefId, string.Empty),
                Decision = "PENDING",
                CreatedBy = Actor,
            });
            generated++;
        }

        campaign.Status = "ACTIVE";
        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        campaign.UpdatedBy = Actor;
        await SaveGovernanceMutationAsync(AuditEventTypes.ReviewCampaignActivated, applicationId, null, new { campaign.Name, itemCount = generated }, cancellationToken);

        List<ReviewItemEntity> items = await DbContext.ReviewItems.AsNoTracking()
            .Where(i => i.CampaignRefId == campaign.Id)
            .OrderBy(i => i.SubjectEmail).ThenBy(i => i.RoleKey)
            .ToListAsync(cancellationToken);
        return Ok(new ReviewCampaignDetailResponse(ToSummary(campaign, items), items.Select(ToItemResponse).ToList()));
    }

    [HttpPost("applications/{applicationId}/review-campaigns/{campaignId:guid}/items/{itemId:guid}/decision")]
    [Authorize(Policy = DelegatedAdminPolicyNames.AssignRoles)]
    public async Task<ActionResult<ReviewItemResponse>> DecideAsync(string applicationId, Guid campaignId, Guid itemId, [FromBody] ReviewItemDecisionRequest request, CancellationToken cancellationToken)
    {
        string decision = (request.Decision ?? string.Empty).Trim().ToUpperInvariant();
        if (decision is not ("KEEP" or "REVOKE" or "NEEDS_INFO" or "PENDING"))
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "decision must be KEEP, REVOKE, NEEDS_INFO or PENDING."));
        }

        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        ReviewCampaignEntity? campaign = applicationRefId is null
            ? null
            : await DbContext.ReviewCampaigns.AsNoTracking()
                .FirstOrDefaultAsync(c => c.ApplicationRefId == applicationRefId.Value && c.Id == campaignId, cancellationToken);
        if (campaign is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "Campaign was not found."));
        }

        if (campaign.Status != "ACTIVE")
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "Decisions can only be recorded on an active campaign."));
        }

        ReviewItemEntity? item = await DbContext.ReviewItems.FirstOrDefaultAsync(i => i.CampaignRefId == campaignId && i.Id == itemId, cancellationToken);
        if (item is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "Review item was not found."));
        }

        item.Decision = decision;
        item.DecisionNote = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        item.DecidedAt = decision == "PENDING" ? null : DateTimeOffset.UtcNow;
        item.DecidedBy = decision == "PENDING" ? null : Actor;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.UpdatedBy = Actor;
        await DbContext.SaveChangesAsync(cancellationToken);
        return Ok(ToItemResponse(item));
    }

    [HttpPost("applications/{applicationId}/review-campaigns/{campaignId:guid}/decisions")]
    [Authorize(Policy = DelegatedAdminPolicyNames.AssignRoles)]
    public async Task<ActionResult<ReviewCampaignDetailResponse>> DecideBulkAsync(string applicationId, Guid campaignId, [FromBody] ReviewItemBulkDecisionRequest request, CancellationToken cancellationToken)
    {
        string decision = (request.Decision ?? string.Empty).Trim().ToUpperInvariant();
        if (decision is not ("KEEP" or "REVOKE" or "NEEDS_INFO" or "PENDING"))
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "decision must be KEEP, REVOKE, NEEDS_INFO or PENDING."));
        }

        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        ReviewCampaignEntity? campaign = applicationRefId is null
            ? null
            : await DbContext.ReviewCampaigns.AsNoTracking()
                .FirstOrDefaultAsync(c => c.ApplicationRefId == applicationRefId.Value && c.Id == campaignId, cancellationToken);
        if (campaign is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "Campaign was not found."));
        }

        if (campaign.Status != "ACTIVE")
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "Decisions can only be recorded on an active campaign."));
        }

        // With no explicit item ids, apply the decision to every still-pending item (e.g. "keep all
        // remaining"); otherwise apply it to just the listed items.
        IReadOnlyList<Guid> ids = request.ItemIds ?? [];
        IQueryable<ReviewItemEntity> query = DbContext.ReviewItems.Where(i => i.CampaignRefId == campaignId);
        query = ids.Count > 0
            ? query.Where(i => ids.Contains(i.Id))
            : query.Where(i => i.Decision == "PENDING");
        List<ReviewItemEntity> items = await query.ToListAsync(cancellationToken);

        string? note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (ReviewItemEntity item in items)
        {
            item.Decision = decision;
            if (note is not null)
            {
                item.DecisionNote = note;
            }
            item.DecidedAt = decision == "PENDING" ? null : now;
            item.DecidedBy = decision == "PENDING" ? null : Actor;
            item.UpdatedAt = now;
            item.UpdatedBy = Actor;
        }

        await DbContext.SaveChangesAsync(cancellationToken);

        List<ReviewItemEntity> allItems = await DbContext.ReviewItems.AsNoTracking()
            .Where(i => i.CampaignRefId == campaignId)
            .OrderBy(i => i.SubjectEmail).ThenBy(i => i.RoleKey)
            .ToListAsync(cancellationToken);
        return Ok(new ReviewCampaignDetailResponse(ToSummary(campaign, allItems), allItems.Select(ToItemResponse).ToList()));
    }

    [HttpPost("applications/{applicationId}/review-campaigns/{campaignId:guid}/finalize")]
    [Authorize(Policy = DelegatedAdminPolicyNames.AssignRoles)]
    public async Task<ActionResult<ReviewCampaignDetailResponse>> FinalizeAsync(string applicationId, Guid campaignId, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        ReviewCampaignEntity? campaign = applicationRefId is null
            ? null
            : await DbContext.ReviewCampaigns.FirstOrDefaultAsync(c => c.ApplicationRefId == applicationRefId.Value && c.Id == campaignId, cancellationToken);
        if (campaign is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "Campaign was not found."));
        }

        if (campaign.Status != "ACTIVE")
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "Only an active campaign can be finalised."));
        }

        List<ReviewItemEntity> items = await DbContext.ReviewItems
            .Where(i => i.CampaignRefId == campaignId)
            .ToListAsync(cancellationToken);
        List<Guid> revokeAssignmentIds = items
            .Where(i => i.Decision == "REVOKE")
            .Select(i => i.AssignmentRefId)
            .ToList();

        // Apply approved revokes through the same state transition + audit event the manual revoke
        // path uses, so certification outcomes are indistinguishable from any other governed revoke.
        int revoked = 0;
        if (revokeAssignmentIds.Count > 0)
        {
            List<AssignmentEntity> toRevoke = await DbContext.Assignments
                .Where(a => a.ApplicationRefId == applicationRefId!.Value && revokeAssignmentIds.Contains(a.Id) && a.State != WorkflowState.Revoked)
                .ToListAsync(cancellationToken);
            foreach (AssignmentEntity assignment in toRevoke)
            {
                assignment.State = WorkflowState.Revoked;
                assignment.RevokedAt = DateTimeOffset.UtcNow;
                assignment.UpdatedAt = DateTimeOffset.UtcNow;
                assignment.UpdatedBy = Actor;
                DbContext.AuditEvents.Add(BuildAuditEvent(
                    AuditEventTypes.AssignmentRevoked,
                    applicationId,
                    assignment.SubjectEmail,
                    oldValue: null,
                    newValue: new { assignment.State, source = "CERTIFICATION", campaign = campaign.Name }));
                revoked++;
            }
        }

        campaign.Status = "CLOSED";
        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        campaign.UpdatedBy = Actor;
        DbContext.AuditEvents.Add(BuildAuditEvent(
            AuditEventTypes.ReviewCampaignFinalized,
            applicationId,
            targetSubjectEmail: null,
            oldValue: null,
            newValue: new { campaign.Name, revoked, itemCount = items.Count }));
        await DbContext.SaveChangesAsync(cancellationToken);

        List<ReviewItemEntity> finalItems = await DbContext.ReviewItems.AsNoTracking()
            .Where(i => i.CampaignRefId == campaignId)
            .OrderBy(i => i.SubjectEmail).ThenBy(i => i.RoleKey)
            .ToListAsync(cancellationToken);
        return Ok(new ReviewCampaignDetailResponse(ToSummary(campaign, finalItems), finalItems.Select(ToItemResponse).ToList()));
    }

    private static ReviewCampaignResponse ToSummary(ReviewCampaignEntity campaign, IEnumerable<ReviewItemEntity> items)
    {
        List<ReviewItemEntity> list = items.ToList();
        return new ReviewCampaignResponse(
            campaign.Id,
            campaign.Name,
            campaign.Status,
            campaign.DueAt,
            list.Count,
            list.Count(i => i.Decision == "PENDING"),
            list.Count(i => i.Decision == "KEEP"),
            list.Count(i => i.Decision == "REVOKE"),
            campaign.CreatedAt);
    }

    private static ReviewItemResponse ToItemResponse(ReviewItemEntity item) =>
        new(item.Id, item.SubjectEmail, item.RoleKey, item.Decision, item.DecisionNote, item.DecidedAt, item.DecidedBy);
}

public sealed class CreateReviewCampaignRequest
{
    public string? Name { get; init; }
    public DateTimeOffset? DueAt { get; init; }
}

public sealed class ReviewItemDecisionRequest
{
    public string? Decision { get; init; }
    public string? Note { get; init; }
}

public sealed class ReviewItemBulkDecisionRequest
{
    public string? Decision { get; init; }
    /// <summary>Items to apply the decision to. When null/empty, applies to every pending item.</summary>
    public IReadOnlyList<Guid>? ItemIds { get; init; }
    public string? Note { get; init; }
}

public sealed record ReviewCampaignResponse(
    Guid Id,
    string Name,
    string Status,
    DateTimeOffset? DueAt,
    int ItemCount,
    int PendingCount,
    int KeepCount,
    int RevokeCount,
    DateTimeOffset CreatedAt);

public sealed record ReviewItemResponse(
    Guid Id,
    string SubjectEmail,
    string RoleKey,
    string Decision,
    string? DecisionNote,
    DateTimeOffset? DecidedAt,
    string? DecidedBy);

public sealed record ReviewCampaignDetailResponse(
    ReviewCampaignResponse Campaign,
    IReadOnlyList<ReviewItemResponse> Items);
