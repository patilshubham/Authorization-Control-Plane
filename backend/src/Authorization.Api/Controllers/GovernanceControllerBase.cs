using System.Text.Json;
using Authorization.Api.Authorization;
using Authorization.Api.Constants;
using Authorization.Api.Errors;
using Authorization.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Controllers;

/// <summary>
/// Shared base for the delegated-admin governance controllers. Centralises the
/// current actor resolution, the standard API error envelope, and the
/// atomic "persist mutation + write audit event" workflow so every governance
/// controller behaves identically.
/// </summary>
public abstract class GovernanceControllerBase : ControllerBase
{
    private const string FallbackActor = "local-admin";

    protected GovernanceControllerBase(AuthorizationDbContext dbContext)
    {
        DbContext = dbContext;
    }

    protected AuthorizationDbContext DbContext { get; }

    protected string Actor => User.Identity?.Name ?? FallbackActor;

    protected string? ActorRole => AuditActorRole.Resolve(User);

    protected ApiErrorEnvelope Error(string code, string message)
    {
        return new ApiErrorEnvelope(new ApiError(code, message), HttpContext.TraceIdentifier);
    }

    /// <summary>
    /// Guards a required text field: returns a 422 validation result when
    /// <paramref name="value"/> is null, empty, or whitespace-only; otherwise null.
    /// The framework's <c>[Required]</c> accepts whitespace such as "   ", so
    /// create/update handlers use this to reject blank text before persisting.
    /// </summary>
    protected IActionResult? RequireText(string? value, string fieldName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, $"{fieldName} is required."))
            : null;
    }

    protected Task<bool> ApplicationExistsAsync(string applicationId, CancellationToken cancellationToken)
    {
        return DbContext.Applications.AnyAsync(entity => entity.ApplicationId == applicationId, cancellationToken);
    }

    /// <summary>Resolves an application's business id to its surrogate guid row id, or null when the application does not exist.</summary>
    protected Task<Guid?> ResolveApplicationRefIdAsync(string applicationId, CancellationToken cancellationToken)
    {
        return DbContext.Applications
            .AsNoTracking()
            .Where(entity => entity.ApplicationId == applicationId)
            .Select(entity => (Guid?)entity.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    protected Task SaveGovernanceMutationAsync(string eventType, string? applicationId, string? targetSubjectEmail, object? newValue, CancellationToken cancellationToken)
        => SaveGovernanceMutationAsync(eventType, applicationId, targetSubjectEmail, oldValue: null, newValue, cancellationToken);

    protected async Task SaveGovernanceMutationAsync(string eventType, string? applicationId, string? targetSubjectEmail, object? oldValue, object? newValue, CancellationToken cancellationToken)
    {
        // Persist the pending entity mutation and its audit event together in a single
        // SaveChangesAsync. On relational providers this executes inside EF Core's implicit
        // transaction, so the mutation and audit row commit atomically (both or neither). EF still
        // back-fills store-generated values (Id, CreatedAt, Version) onto the tracked entity after
        // the save, so callers may safely map the entity into a response afterwards.
        DbContext.AuditEvents.Add(BuildAuditEvent(eventType, applicationId, targetSubjectEmail, oldValue, newValue));
        await DbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Builds (but does not persist or track) an audit event stamped with the current actor, role,
    /// timestamp, and correlation id. Bulk handlers that add many audit rows before a single
    /// SaveChangesAsync use this to avoid repeating that scaffolding; single mutations should use
    /// SaveGovernanceMutationAsync, which builds the event and saves atomically.
    /// </summary>
    protected AuditEventEntity BuildAuditEvent(string eventType, string? applicationId, string? targetSubjectEmail, object? oldValue, object? newValue)
    {
        return new AuditEventEntity
        {
            EventType = eventType,
            ApplicationId = applicationId,
            ActorEmail = Actor,
            ActorRole = ActorRole,
            TargetSubjectEmail = targetSubjectEmail,
            Timestamp = DateTimeOffset.UtcNow,
            OldValue = oldValue is null ? null : JsonSerializer.Serialize(oldValue),
            NewValue = newValue is null ? null : JsonSerializer.Serialize(newValue),
            CorrelationId = HttpContext.TraceIdentifier,
        };
    }

    /// <summary>Resolves a role's surrogate id to its business key, or empty string when the role no longer exists.</summary>
    protected async Task<string> RoleKeyByIdAsync(Guid roleRefId, CancellationToken cancellationToken)
    {
        return await DbContext.Roles.AsNoTracking()
            .Where(role => role.Id == roleRefId)
            .Select(role => role.RoleKey)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
    }

    /// <summary>Resolves a permission's surrogate id to its business key, or empty string when the permission no longer exists.</summary>
    protected async Task<string> PermissionKeyByIdAsync(Guid permissionRefId, CancellationToken cancellationToken)
    {
        return await DbContext.Permissions.AsNoTracking()
            .Where(permission => permission.Id == permissionRefId)
            .Select(permission => permission.PermissionKey)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
    }
}
