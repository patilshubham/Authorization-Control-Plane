using System.Text;
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
public sealed class AssignmentsController : GovernanceControllerBase
{
    public AssignmentsController(AuthorizationDbContext dbContext)
        : base(dbContext)
    {
    }

    [HttpGet("applications/{applicationId}/assignments")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ReadOnlyView)]
    public async Task<PagedResult<AssignmentResponse>> GetAssignmentsAsync(string applicationId, [FromQuery] string? q, [FromQuery] string? state, [FromQuery] string? expiry, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return new PagedResult<AssignmentResponse>([], 1, 0, 0);
        }

        List<AssignmentEntity> assignments = await DbContext.Assignments
            .AsNoTracking()
            .Where(entity => entity.ApplicationRefId == applicationRefId.Value)
            .OrderByDescending(entity => entity.CreatedAt)
            .ToListAsync(cancellationToken);

        Dictionary<Guid, string> roleKeyById = await DbContext.Roles
            .AsNoTracking()
            .Where(entity => entity.ApplicationRefId == applicationRefId.Value)
            .ToDictionaryAsync(entity => entity.Id, entity => entity.RoleKey, cancellationToken);

        // State filter uses the *derived* display status (ACTIVE/EXPIRED/REVOKED) so it matches the
        // status the UI shows, not the raw stored state (an ACTIVE row past its expiry reads EXPIRED).
        if (!string.IsNullOrWhiteSpace(state))
        {
            assignments = assignments
                .Where(a => string.Equals(AssignmentDisplayStatus.Resolve(a.State, a.ValidUntil, a.RevokedAt), state, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(expiry))
        {
            assignments = assignments.Where(a => ExpiryBucket(a.ValidUntil) == expiry.Trim().ToLowerInvariant()).ToList();
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            string needle = q.Trim();
            assignments = assignments
                .Where(a => (a.SubjectEmail ?? string.Empty).Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || roleKeyById.GetValueOrDefault(a.RoleRefId, string.Empty).Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        PageRequest paging = PageRequest.From(page, pageSize);
        int total = assignments.Count;
        if (paging.Enabled)
        {
            assignments = assignments.Skip(paging.Skip).Take(paging.PageSize).ToList();
        }

        List<AssignmentResponse> items = assignments
            .Select(entity => AssignmentResponse.From(entity, roleKeyById.GetValueOrDefault(entity.RoleRefId, string.Empty)))
            .ToList();
        return new PagedResult<AssignmentResponse>(items, paging.Enabled ? paging.Page : 1, paging.Enabled ? paging.PageSize : total, total);
    }

    /// <summary>
    /// Buckets an assignment by expiry window, mirroring the portal's expiry filter:
    /// none (no expiry), expired (past), soon (within 7 days) or later.
    /// </summary>
    private static string ExpiryBucket(DateTimeOffset? validUntil)
    {
        if (validUntil is null)
        {
            return "none";
        }

        TimeSpan remaining = validUntil.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "expired";
        }

        return remaining <= TimeSpan.FromDays(7) ? "soon" : "later";
    }

    [HttpGet("applications/{applicationId}/assignments/summary")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ReadOnlyView)]
    public async Task<AssignmentSummaryResponse> GetAssignmentsSummaryAsync(string applicationId, [FromQuery] string? state, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return new AssignmentSummaryResponse(0, []);
        }

        var rows = await DbContext.Assignments
            .AsNoTracking()
            .Where(entity => entity.ApplicationRefId == applicationRefId.Value)
            .Select(entity => new { entity.State, entity.ValidUntil, entity.RevokedAt })
            .ToListAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(state))
        {
            rows = rows
                .Where(r => string.Equals(AssignmentDisplayStatus.Resolve(r.State, r.ValidUntil, r.RevokedAt), state, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        List<AssignmentStateCount> states = rows
            .GroupBy(r => AssignmentDisplayStatus.Resolve(r.State, r.ValidUntil, r.RevokedAt))
            .Select(g => new AssignmentStateCount(g.Key, g.Count()))
            .OrderBy(s => s.State)
            .ToList();
        return new AssignmentSummaryResponse(rows.Count, states);
    }

    [HttpPost("applications/{applicationId}/assignments")]
    [Authorize(Policy = DelegatedAdminPolicyNames.AssignRoles)]
    public async Task<IActionResult> CreateAssignmentAsync(string applicationId, [FromBody] CreateAssignmentRequest request, CancellationToken cancellationToken)
    {
        if (request.ValidUntil.HasValue && request.ValidUntil.Value <= DateTimeOffset.UtcNow)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "validUntil must be a future date."));
        }

        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "Application was not found."));
        }

        // Privilege is always derived from the role's own record, never trusted from client input —
        // otherwise a caller could self-report privileged: false to bypass the time-boxing rule below.
        RoleEntity? role = await DbContext.Roles
            .FirstOrDefaultAsync(r => r.ApplicationRefId == applicationRefId.Value && r.RoleKey == request.RoleKey, cancellationToken);
        if (role is null)
        {
            return NotFound(Error(GovernanceErrorCodes.RoleNotFound, "Role was not found in this application."));
        }

        if (role.Privileged && request.ValidUntil is null)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.PrivilegedAssignmentRequiresExpiry, "Privileged assignments must be time-boxed."));
        }

        Guid roleRefId = role.Id;

        // De-duplicate active USER grants: a subject either actively holds a role or it does not.
        // Revoked/expired grants are history and never block a legitimate re-grant. This mirrors the
        // CSV-import rules so manual and bulk paths behave identically.
        if (string.Equals(request.SubjectType, "USER", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(request.SubjectEmail))
        {
            AssignmentEntity? existingActive = await FindActiveUserAssignmentAsync(
                applicationRefId.Value, request.SubjectEmail!, roleRefId, cancellationToken);
            if (existingActive is not null)
            {
                if (SameExpiry(existingActive.ValidUntil, request.ValidUntil))
                {
                    return Conflict(Error(
                        GovernanceErrorCodes.AssignmentExists,
                        "This subject already has an active grant of this role. Edit the existing assignment to change its expiry."));
                }

                // Consolidate onto the single active record — keep the later expiry instead of
                // creating a second grant for the same subject and role.
                DateTimeOffset? consolidated = LaterExpiry(existingActive.ValidUntil, request.ValidUntil);
                object before = new { existingActive.ValidUntil, existingActive.Reason };
                existingActive.ValidUntil = consolidated;
                if (!string.IsNullOrWhiteSpace(request.Reason))
                {
                    existingActive.Reason = request.Reason;
                }
                existingActive.UpdatedBy = Actor;
                existingActive.UpdatedAt = DateTimeOffset.UtcNow;
                await SaveGovernanceMutationAsync(
                    AuditEventTypes.AssignmentUpdated,
                    applicationId,
                    existingActive.SubjectEmail,
                    before,
                    new { existingActive.ValidUntil, RoleKey = request.RoleKey, consolidated = true },
                    cancellationToken);
                return Ok(AssignmentResponse.From(existingActive, request.RoleKey));
            }
        }

        var entity = new AssignmentEntity
        {
            ApplicationRefId = applicationRefId.Value,
            SubjectType = request.SubjectType,
            SubjectEmail = request.SubjectEmail,
            GroupId = request.GroupId,
            RoleRefId = roleRefId,
            ResourceType = request.ResourceType,
            ResourceId = request.ResourceId,
            ValidFrom = request.ValidFrom ?? DateTimeOffset.UtcNow,
            ValidUntil = request.ValidUntil,
            Source = request.Source,
            State = WorkflowState.Active,
            Reason = request.Reason,
            CreatedBy = Actor,
        };

        DbContext.Assignments.Add(entity);
        object created = new { entity.SubjectType, entity.SubjectEmail, entity.GroupId, RoleKey = request.RoleKey, entity.ResourceType, entity.ResourceId, entity.ValidFrom, entity.ValidUntil, entity.Source, entity.State, entity.Reason };
        await SaveGovernanceMutationAsync(AuditEventTypes.AssignmentCreated, applicationId, entity.SubjectEmail, created, cancellationToken);
        return Created($"/v1/admin/applications/{applicationId}/assignments/{entity.Id}", AssignmentResponse.From(entity, request.RoleKey));
    }

    [HttpPost("applications/{applicationId}/assignments/break-glass")]
    [Authorize(Policy = DelegatedAdminPolicyNames.AssignRoles)]
    public async Task<IActionResult> BreakGlassAsync(string applicationId, [FromBody] BreakGlassRequest request, CancellationToken cancellationToken)
    {
        string subjectEmail = (request.SubjectEmail ?? string.Empty).Trim();
        string roleKey = (request.RoleKey ?? string.Empty).Trim();
        string reason = (request.Reason ?? string.Empty).Trim();
        if (subjectEmail.Length == 0 || roleKey.Length == 0)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "subjectEmail and roleKey are required."));
        }

        // A justification is mandatory for emergency access, and the window is tightly bounded
        // (1–24h) so break-glass grants are always short-lived and auto-expire.
        if (reason.Length == 0)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "A justification reason is required for break-glass access."));
        }

        int hours = request.DurationHours is int h && h > 0 ? Math.Min(h, 24) : 0;
        if (hours == 0)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "durationHours must be between 1 and 24."));
        }

        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "Application was not found."));
        }

        Guid? roleRefId = await DbContext.Roles
            .Where(role => role.ApplicationRefId == applicationRefId.Value && role.RoleKey == roleKey)
            .Select(role => (Guid?)role.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (roleRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.RoleNotFound, "Role was not found in this application."));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var entity = new AssignmentEntity
        {
            ApplicationRefId = applicationRefId.Value,
            SubjectType = "USER",
            SubjectEmail = subjectEmail,
            RoleRefId = roleRefId.Value,
            ValidFrom = now,
            ValidUntil = now.AddHours(hours),
            Source = "EMERGENCY",
            State = WorkflowState.Active,
            Reason = reason,
            CreatedBy = Actor,
        };

        DbContext.Assignments.Add(entity);
        // A distinct, high-visibility audit event. The grant auto-expires at ValidUntil via the same
        // validity check the runtime engine already applies — no standing emergency access.
        object created = new { entity.SubjectEmail, RoleKey = roleKey, entity.ValidUntil, entity.Source, entity.Reason };
        await SaveGovernanceMutationAsync(AuditEventTypes.BreakGlassActivated, applicationId, entity.SubjectEmail, created, cancellationToken);
        return Created($"/v1/admin/applications/{applicationId}/assignments/{entity.Id}", AssignmentResponse.From(entity, roleKey));
    }

    [HttpGet("applications/{applicationId}/assignments/export")]
    [Authorize(Policy = DelegatedAdminPolicyNames.ReadOnlyView)]
    public async Task<IActionResult> ExportAssignmentsAsync(string applicationId, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "Application was not found."));
        }

        List<AssignmentEntity> assignments = await DbContext.Assignments.AsNoTracking()
            .Where(entity => entity.ApplicationRefId == applicationRefId.Value)
            .OrderByDescending(entity => entity.CreatedAt)
            .ToListAsync(cancellationToken);
        Dictionary<Guid, string> roleKeyById = await DbContext.Roles.AsNoTracking()
            .Where(entity => entity.ApplicationRefId == applicationRefId.Value)
            .ToDictionaryAsync(entity => entity.Id, entity => entity.RoleKey, cancellationToken);

        var builder = new StringBuilder();
        builder.AppendLine("subjectEmail,roleKey,state,validFrom,validUntil,source,reason");
        foreach (AssignmentEntity a in assignments)
        {
            string state = AssignmentDisplayStatus.Resolve(a.State, a.ValidUntil, a.RevokedAt);
            builder.AppendLine(string.Join(',', new[]
            {
                CsvField(a.SubjectEmail),
                CsvField(roleKeyById.GetValueOrDefault(a.RoleRefId, string.Empty)),
                CsvField(state),
                CsvField(a.ValidFrom.ToString("O")),
                CsvField(a.ValidUntil?.ToString("O")),
                CsvField(a.Source),
                CsvField(a.Reason),
            }));
        }

        byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
        string fileName = $"{applicationId}-assignments-{DateTimeOffset.UtcNow:yyyyMMdd}.csv";
        return File(bytes, "text/csv", fileName);
    }

    [HttpPost("applications/{applicationId}/assignments/import")]
    [Authorize(Policy = DelegatedAdminPolicyNames.AssignRoles)]
    public async Task<ActionResult<AssignmentImportResponse>> ImportAssignmentsAsync(string applicationId, [FromBody] AssignmentImportRequest request, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "Application was not found."));
        }

        IReadOnlyList<AssignmentImportRow> rows = request.Rows ?? [];
        if (rows.Count == 0)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "There are no rows to import."));
        }

        if (rows.Count > 500)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "Import is limited to 500 rows at a time."));
        }

        List<RoleEntity> roles = await DbContext.Roles.AsNoTracking()
            .Where(role => role.ApplicationRefId == applicationRefId.Value)
            .ToListAsync(cancellationToken);
        Dictionary<string, RoleEntity> roleByKey = roles.ToDictionary(role => role.RoleKey, role => role, StringComparer.OrdinalIgnoreCase);

        // Existing ACTIVE user grants, indexed by (lowercased email, roleRefId). Loaded tracked so an
        // UPDATE row (expiry consolidation) can be applied in place. Revoked/expired history is ignored.
        List<AssignmentEntity> existingUserAssignments = await DbContext.Assignments
            .Where(a => a.ApplicationRefId == applicationRefId.Value && a.SubjectType == "USER" && a.SubjectEmail != null)
            .ToListAsync(cancellationToken);
        var activeByKey = new Dictionary<(string Email, Guid Role), AssignmentEntity>();
        foreach (AssignmentEntity existing in existingUserAssignments)
        {
            if (AssignmentDisplayStatus.Resolve(existing.State, existing.ValidUntil, existing.RevokedAt) != AssignmentDisplayStatus.Active)
            {
                continue;
            }

            activeByKey.TryAdd((existing.SubjectEmail!.Trim().ToLowerInvariant(), existing.RoleRefId), existing);
        }

        // Pass 1: validate each row and fold in-file duplicates (same subject+role) into a single
        // pending change, keeping the later expiry. A fixed-size buffer preserves original row order.
        var resultBuffer = new AssignmentImportResultRow?[rows.Count];
        var pendingByKey = new Dictionary<(string Email, Guid Role), ImportPending>();
        for (int i = 0; i < rows.Count; i++)
        {
            int rowNumber = i + 1;
            AssignmentImportRow row = rows[i];
            string email = (row.SubjectEmail ?? string.Empty).Trim();
            string roleKey = (row.RoleKey ?? string.Empty).Trim();

            if (email.Length == 0 || roleKey.Length == 0)
            {
                resultBuffer[i] = new AssignmentImportResultRow(rowNumber, email, roleKey, "ERROR", "subjectEmail and roleKey are both required.");
                continue;
            }

            if (!roleByKey.TryGetValue(roleKey, out RoleEntity? role))
            {
                resultBuffer[i] = new AssignmentImportResultRow(rowNumber, email, roleKey, "ERROR", $"Unknown role '{roleKey}' in this application.");
                continue;
            }

            if (row.ValidUntil.HasValue && row.ValidUntil.Value <= DateTimeOffset.UtcNow)
            {
                resultBuffer[i] = new AssignmentImportResultRow(rowNumber, email, roleKey, "ERROR", "validUntil must be a future date.");
                continue;
            }

            if (role.Privileged && row.ValidUntil is null)
            {
                resultBuffer[i] = new AssignmentImportResultRow(rowNumber, email, roleKey, "ERROR", "Privileged roles must be time-boxed — provide a validUntil.");
                continue;
            }

            var key = (email.ToLowerInvariant(), role.Id);
            if (pendingByKey.TryGetValue(key, out ImportPending? chosen))
            {
                // In-file duplicate: keep the later expiry on the already-chosen row, skip this one.
                chosen.TargetExpiry = LaterExpiry(chosen.TargetExpiry, row.ValidUntil);
                resultBuffer[i] = new AssignmentImportResultRow(rowNumber, email, roleKey, "SKIP", $"Duplicate of row #{chosen.RowNumber} in this file — kept the later expiry.");
                continue;
            }

            activeByKey.TryGetValue(key, out AssignmentEntity? existingActive);
            pendingByKey[key] = new ImportPending
            {
                RowNumber = rowNumber,
                Email = email,
                RoleKey = roleKey,
                Role = role,
                Existing = existingActive,
                TargetExpiry = row.ValidUntil,
            };
            // The CREATE/UPDATE/SKIP decision is finalised in pass 2, once in-file expiries are folded.
        }

        // Pass 2: finalise the decision for each chosen row and stage the persistence work.
        var creates = new List<(AssignmentEntity Entity, string RoleKey)>();
        var updates = new List<(AssignmentEntity Entity, string RoleKey, DateTimeOffset? Before, DateTimeOffset? After)>();
        foreach (ImportPending pendingChange in pendingByKey.Values)
        {
            int idx = pendingChange.RowNumber - 1;
            if (pendingChange.Existing is null)
            {
                resultBuffer[idx] = new AssignmentImportResultRow(pendingChange.RowNumber, pendingChange.Email, pendingChange.RoleKey, "CREATE", "New grant.");
                creates.Add((new AssignmentEntity
                {
                    ApplicationRefId = applicationRefId.Value,
                    SubjectType = "USER",
                    SubjectEmail = pendingChange.Email,
                    RoleRefId = pendingChange.Role.Id,
                    ValidFrom = DateTimeOffset.UtcNow,
                    ValidUntil = pendingChange.TargetExpiry,
                    Source = "IMPORT",
                    State = WorkflowState.Active,
                    CreatedBy = Actor,
                }, pendingChange.RoleKey));
            }
            else if (SameExpiry(pendingChange.Existing.ValidUntil, pendingChange.TargetExpiry))
            {
                resultBuffer[idx] = new AssignmentImportResultRow(pendingChange.RowNumber, pendingChange.Email, pendingChange.RoleKey, "SKIP", "Subject already has this active grant.");
            }
            else
            {
                DateTimeOffset? later = LaterExpiry(pendingChange.Existing.ValidUntil, pendingChange.TargetExpiry);
                if (SameExpiry(later, pendingChange.Existing.ValidUntil))
                {
                    resultBuffer[idx] = new AssignmentImportResultRow(pendingChange.RowNumber, pendingChange.Email, pendingChange.RoleKey, "SKIP", "Existing grant already has an equal or later expiry.");
                }
                else
                {
                    resultBuffer[idx] = new AssignmentImportResultRow(pendingChange.RowNumber, pendingChange.Email, pendingChange.RoleKey, "UPDATE", "Extends the existing grant's expiry.");
                    updates.Add((pendingChange.Existing, pendingChange.RoleKey, pendingChange.Existing.ValidUntil, later));
                }
            }
        }

        List<AssignmentImportResultRow> results = resultBuffer.Select(r => r!).ToList();
        int created = results.Count(r => r.Status == "CREATE");
        int updated = results.Count(r => r.Status == "UPDATE");
        int skipped = results.Count(r => r.Status == "SKIP");
        int failed = results.Count(r => r.Status == "ERROR");
        int applied = 0;

        // Dry-run returns the validated preview without persisting anything. On apply, new grants are
        // created and consolidations update the existing grant — both through audited events.
        if (!request.DryRun && (creates.Count > 0 || updates.Count > 0))
        {
            foreach ((AssignmentEntity entity, string roleKey) in creates)
            {
                DbContext.Assignments.Add(entity);
                DbContext.AuditEvents.Add(BuildAuditEvent(
                    AuditEventTypes.AssignmentCreated,
                    applicationId,
                    entity.SubjectEmail,
                    oldValue: null,
                    newValue: new { entity.SubjectType, entity.SubjectEmail, RoleKey = roleKey, entity.ValidUntil, entity.Source, entity.State }));
            }

            foreach ((AssignmentEntity entity, string roleKey, DateTimeOffset? before, DateTimeOffset? after) in updates)
            {
                entity.ValidUntil = after;
                entity.UpdatedBy = Actor;
                entity.UpdatedAt = DateTimeOffset.UtcNow;
                DbContext.AuditEvents.Add(BuildAuditEvent(
                    AuditEventTypes.AssignmentUpdated,
                    applicationId,
                    entity.SubjectEmail,
                    oldValue: new { ValidUntil = before },
                    newValue: new { entity.SubjectEmail, RoleKey = roleKey, ValidUntil = after, Source = "IMPORT", consolidated = true }));
            }

            await DbContext.SaveChangesAsync(cancellationToken);
            applied = creates.Count + updates.Count;
        }

        return Ok(new AssignmentImportResponse(request.DryRun, results.Count, created + updated, applied, failed, created, updated, skipped, results));
    }

    // Returns the single currently-ACTIVE grant of a role for a user subject, if one exists. Used
    // to de-duplicate manual creates and CSV imports. Case-insensitive on the subject email.
    private async Task<AssignmentEntity?> FindActiveUserAssignmentAsync(Guid applicationRefId, string subjectEmail, Guid roleRefId, CancellationToken cancellationToken)
    {
        string needle = subjectEmail.Trim().ToLowerInvariant();
        List<AssignmentEntity> candidates = await DbContext.Assignments
            .Where(a => a.ApplicationRefId == applicationRefId
                && a.RoleRefId == roleRefId
                && a.SubjectType == "USER"
                && a.SubjectEmail != null
                && a.SubjectEmail.ToLower() == needle)
            .ToListAsync(cancellationToken);
        return candidates.FirstOrDefault(a =>
            AssignmentDisplayStatus.Resolve(a.State, a.ValidUntil, a.RevokedAt) == AssignmentDisplayStatus.Active);
    }

    // Two grants are duplicates when their expiry is identical (both permanent, or the same instant).
    private static bool SameExpiry(DateTimeOffset? a, DateTimeOffset? b)
        => a is null ? b is null : (b is not null && a.Value == b.Value);

    // The later of two expiries. A null expiry means "permanent", which is the latest possible, so
    // consolidating never accidentally shortens an existing grant.
    private static DateTimeOffset? LaterExpiry(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (a is null || b is null)
        {
            return null;
        }

        return b.Value > a.Value ? b : a;
    }

    // A staged import change for a unique (subject, role): either a new grant (Existing is null) or a
    // consolidation onto the existing active grant. TargetExpiry folds in-file duplicate expiries.
    private sealed class ImportPending
    {
        public required int RowNumber { get; init; }
        public required string Email { get; init; }
        public required string RoleKey { get; init; }
        public required RoleEntity Role { get; init; }
        public AssignmentEntity? Existing { get; set; }
        public DateTimeOffset? TargetExpiry { get; set; }
    }

    // CSV-escape a field, guarding against formula/CSV injection: a value beginning with a
    // spreadsheet control character (= + - @) is prefixed with a single quote so it is treated as
    // text, and any value containing a comma, quote or newline is wrapped in doubled quotes.
    private static string CsvField(string? value)
    {
        string text = value ?? string.Empty;
        if (text.Length > 0 && (text[0] is '=' or '+' or '-' or '@'))
        {
            text = "'" + text;
        }

        if (text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r'))
        {
            text = "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        return text;
    }

    [HttpPost("applications/{applicationId}/assignments/{assignmentId:guid}/revoke")]
    [Authorize(Policy = DelegatedAdminPolicyNames.AssignRoles)]
    public async Task<IActionResult> RevokeAssignmentAsync(string applicationId, Guid assignmentId, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        AssignmentEntity? entity = applicationRefId is null
            ? null
            : await DbContext.Assignments.FirstOrDefaultAsync(assignment => assignment.ApplicationRefId == applicationRefId.Value && assignment.Id == assignmentId, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.AssignmentNotFound, "Assignment was not found."));
        }

        object oldValue = new { entity.State, entity.RevokedAt };
        entity.State = WorkflowState.Revoked;
        entity.RevokedAt = DateTimeOffset.UtcNow;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedBy = Actor;
        await SaveGovernanceMutationAsync(AuditEventTypes.AssignmentRevoked, applicationId, entity.SubjectEmail, oldValue, new { entity.State, entity.RevokedAt }, cancellationToken);
        string revokedRoleKey = await RoleKeyByIdAsync(entity.RoleRefId, cancellationToken);
        return Ok(AssignmentResponse.From(entity, revokedRoleKey));
    }

    [HttpPost("applications/{applicationId}/assignments/{assignmentId:guid}/extend")]
    [Authorize(Policy = DelegatedAdminPolicyNames.AssignRoles)]
    public async Task<IActionResult> ExtendAssignmentAsync(string applicationId, Guid assignmentId, [FromBody] ExtendAssignmentRequest request, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        AssignmentEntity? entity = applicationRefId is null
            ? null
            : await DbContext.Assignments.FirstOrDefaultAsync(assignment => assignment.ApplicationRefId == applicationRefId.Value && assignment.Id == assignmentId, cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.AssignmentNotFound, "Assignment was not found."));
        }

        object oldValue = new { entity.ValidUntil };
        entity.ValidUntil = request.ValidUntil;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedBy = Actor;
        await SaveGovernanceMutationAsync(AuditEventTypes.AssignmentExtended, applicationId, entity.SubjectEmail, oldValue, new { entity.ValidUntil }, cancellationToken);
        string extendedRoleKey = await RoleKeyByIdAsync(entity.RoleRefId, cancellationToken);
        return Ok(AssignmentResponse.From(entity, extendedRoleKey));
    }

    [HttpPut("applications/{applicationId}/assignments/{assignmentId:guid}")]
    [Authorize(Policy = DelegatedAdminPolicyNames.AssignRoles)]
    public async Task<IActionResult> UpdateAssignmentAsync(string applicationId, Guid assignmentId, [FromBody] UpdateAssignmentRequest request, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.AssignmentNotFound, "Assignment was not found."));
        }

        AssignmentEntity? entity = await DbContext.Assignments.FirstOrDefaultAsync(
            assignment => assignment.ApplicationRefId == applicationRefId.Value && assignment.Id == assignmentId,
            cancellationToken);
        if (entity is null)
        {
            return NotFound(Error(GovernanceErrorCodes.AssignmentNotFound, "Assignment was not found."));
        }

        if (entity.State == WorkflowState.Revoked)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.AssignmentRevoked, "Revoked assignments cannot be edited. Grant a new assignment instead."));
        }

        Dictionary<Guid, string> currentRoleKeyById = await DbContext.Roles
            .AsNoTracking()
            .Where(role => role.ApplicationRefId == applicationRefId.Value)
            .ToDictionaryAsync(role => role.Id, role => role.RoleKey, cancellationToken);
        string currentRoleKey = currentRoleKeyById.GetValueOrDefault(entity.RoleRefId, string.Empty);

        string roleKey = string.IsNullOrWhiteSpace(request.RoleKey) ? currentRoleKey : request.RoleKey.Trim();
        RoleEntity? role = await DbContext.Roles.FirstOrDefaultAsync(r => r.ApplicationRefId == applicationRefId.Value && r.RoleKey == roleKey, cancellationToken);
        if (role is null)
        {
            return NotFound(Error(GovernanceErrorCodes.RoleNotFound, "Role was not found in this application."));
        }

        if (request.ValidUntil.HasValue && request.ValidUntil.Value <= DateTimeOffset.UtcNow)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "validUntil must be a future date."));
        }

        if (role.Privileged && request.ValidUntil is null)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.PrivilegedAssignmentRequiresExpiry, "Privileged roles must be time-boxed. Set an expiry date."));
        }

        // Changing the role must not create a second active grant of the same role for the subject.
        if (role.Id != entity.RoleRefId
            && string.Equals(entity.SubjectType, "USER", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entity.SubjectEmail))
        {
            AssignmentEntity? clash = await FindActiveUserAssignmentAsync(applicationRefId.Value, entity.SubjectEmail!, role.Id, cancellationToken);
            if (clash is not null && clash.Id != entity.Id)
            {
                return Conflict(Error(GovernanceErrorCodes.AssignmentExists, "This subject already has an active grant of that role."));
            }
        }

        object oldValue = new { RoleKey = currentRoleKey, entity.ValidUntil, entity.Reason };
        entity.RoleRefId = role.Id;
        entity.ValidUntil = request.ValidUntil;
        entity.Reason = request.Reason;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedBy = Actor;
        await SaveGovernanceMutationAsync(AuditEventTypes.AssignmentUpdated, applicationId, entity.SubjectEmail, oldValue, new { RoleKey = roleKey, entity.ValidUntil, entity.Reason }, cancellationToken);
        return Ok(AssignmentResponse.From(entity, roleKey));
    }

    [HttpPost("applications/{applicationId}/assignments/{assignmentId:guid}/attributes")]
    [Authorize(Policy = DelegatedAdminPolicyNames.AssignRoles)]
    public async Task<IActionResult> CreateAssignmentAttributeAsync(string applicationId, Guid assignmentId, [FromBody] CreateAssignmentAttributeRequest request, CancellationToken cancellationToken)
    {
        Guid? applicationRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (applicationRefId is null || !await DbContext.Assignments.AnyAsync(assignment => assignment.ApplicationRefId == applicationRefId.Value && assignment.Id == assignmentId, cancellationToken))
        {
            return NotFound(Error(GovernanceErrorCodes.AssignmentNotFound, "Assignment was not found."));
        }

        var entity = new AssignmentAttributeEntity
        {
            AssignmentId = assignmentId,
            Name = request.Name,
            Value = request.ValueJson,
            ValueType = request.ValueType,
            CreatedBy = Actor,
        };

        DbContext.AssignmentAttributes.Add(entity);
        object created = new { entity.Name, entity.Value, entity.ValueType };
        await SaveGovernanceMutationAsync(AuditEventTypes.AssignmentAttributeCreated, applicationId, null, created, cancellationToken);
        return Created($"/v1/admin/applications/{applicationId}/assignments/{assignmentId}/attributes/{entity.Id}", entity);
    }
}

/// <summary>Bulk assignment import request. Rows are parsed from CSV client-side; the server validates each.</summary>
public sealed class AssignmentImportRequest
{
    /// <summary>When true (default), validate and preview only — nothing is persisted.</summary>
    public bool DryRun { get; init; } = true;
    public IReadOnlyList<AssignmentImportRow>? Rows { get; init; }
}

public sealed class AssignmentImportRow
{
    public string? SubjectEmail { get; init; }
    public string? RoleKey { get; init; }
    public DateTimeOffset? ValidUntil { get; init; }
}

/// <summary>
/// Per-row import outcome. Status is one of: CREATE (a new grant), UPDATE (an existing grant's
/// expiry consolidated), SKIP (a duplicate that made no change) or ERROR (rejected, with a message).
/// </summary>
public sealed record AssignmentImportResultRow(int Row, string SubjectEmail, string RoleKey, string Status, string? Message);

public sealed record AssignmentImportResponse(
    bool DryRun,
    int Total,
    int Valid,
    int Applied,
    int Failed,
    int Created,
    int Updated,
    int Skipped,
    IReadOnlyList<AssignmentImportResultRow> Results);

/// <summary>Break-glass (emergency) access request: a short, mandatory-reason privileged grant.</summary>
public sealed class BreakGlassRequest
{
    public string? SubjectEmail { get; init; }
    public string? RoleKey { get; init; }
    public string? Reason { get; init; }
    /// <summary>Auto-expiry window in hours (1–24).</summary>
    public int? DurationHours { get; init; }
}
