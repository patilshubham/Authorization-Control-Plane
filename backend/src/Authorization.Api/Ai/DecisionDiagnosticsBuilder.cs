using System.Text.Json;
using Authorization.Ai;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Ai;

/// <summary>
/// Gathers read-only facts from the authorization store so the decision explainer can pinpoint the
/// exact cause of a denial (a misspelled subject email, a missing role grant, a blocking policy, a
/// resource/action that is not defined, …) instead of speculating. All queries are read-only and
/// scoped to a single application; nothing here participates in the enforcement path.
/// </summary>
public sealed class DecisionDiagnosticsBuilder
{
    // Upper bound on the edit distance for two emails to be considered a likely typo of each other.
    private const int SimilarityThreshold = 2;

    private readonly AuthorizationDbContext dbContext;
    private readonly TimeProvider timeProvider;

    public DecisionDiagnosticsBuilder(AuthorizationDbContext dbContext, TimeProvider timeProvider)
    {
        this.dbContext = dbContext;
        this.timeProvider = timeProvider;
    }

    public async Task<DecisionDiagnostics> BuildAsync(
        Guid applicationRefId,
        string subjectType,
        string? subjectEmail,
        string resourceType,
        string action,
        IReadOnlyList<string> matchedPolicies,
        IReadOnlyList<string> providedContextKeys,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        // 1 — Permission existence and the surrounding resource/action landscape (typo hints).
        List<PermissionEntity> appPermissions = await dbContext.Permissions
            .AsNoTracking()
            .Where(permission => permission.ApplicationRefId == applicationRefId && permission.Status == "ACTIVE")
            .ToListAsync(cancellationToken);

        PermissionEntity? permission = appPermissions.FirstOrDefault(entity =>
            string.Equals(entity.Resource, resourceType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entity.Action, action, StringComparison.OrdinalIgnoreCase));
        bool permissionExists = permission is not null;
        bool resourceTypeKnown = appPermissions.Any(entity =>
            string.Equals(entity.Resource, resourceType, StringComparison.OrdinalIgnoreCase));

        List<string> availableActionsForResourceType = appPermissions
            .Where(entity => string.Equals(entity.Resource, resourceType, StringComparison.OrdinalIgnoreCase))
            .Select(entity => entity.Action)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<string> knownResourceTypes = appPermissions
            .Select(entity => entity.Resource)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 2 — Subject / assignment lifecycle diagnostics.
        List<AssignmentEntity> subjectAssignments = string.IsNullOrWhiteSpace(subjectEmail)
            ? []
            : await dbContext.Assignments
                .AsNoTracking()
                .Where(assignment => assignment.ApplicationRefId == applicationRefId
                    && assignment.SubjectType == subjectType
                    && assignment.SubjectEmail == subjectEmail)
                .ToListAsync(cancellationToken);
        bool subjectKnown = subjectAssignments.Count > 0;

        List<AssignmentEntity> activeAssignments = subjectAssignments
            .Where(assignment => assignment.State == "ACTIVE"
                && assignment.RevokedAt is null
                && assignment.ValidFrom <= now
                && (assignment.ValidUntil is null || assignment.ValidUntil > now))
            .ToList();
        int revokedAssignmentCount = subjectAssignments.Count(assignment =>
            assignment.State == "REVOKED" || assignment.RevokedAt is not null);
        int expiredAssignmentCount = subjectAssignments.Count(assignment =>
            assignment.RevokedAt is null
            && (assignment.State == "EXPIRED" || (assignment.ValidUntil is not null && assignment.ValidUntil <= now)));
        int pendingAssignmentCount = subjectAssignments.Count(assignment =>
            assignment.RevokedAt is null && assignment.ValidFrom > now);

        Dictionary<Guid, string> roleKeyById = await dbContext.Roles
            .AsNoTracking()
            .Where(role => role.ApplicationRefId == applicationRefId)
            .ToDictionaryAsync(role => role.Id, role => role.RoleKey, cancellationToken);

        List<string> subjectRoleKeys = activeAssignments
            .Select(assignment => assignment.RoleRefId)
            .Where(roleKeyById.ContainsKey)
            .Select(id => roleKeyById[id])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 3 — Which roles grant the permission, and 4 — policies attached to it (only meaningful
        // when the permission actually exists).
        List<string> rolesGrantingPermission = [];
        List<PolicyDiagnostic> relevantPolicies = [];
        List<string> referencedContextAttributes = [];
        if (permission is not null)
        {
            List<Guid> grantingRoleIds = await dbContext.RolePermissions
                .AsNoTracking()
                .Where(mapping => mapping.ApplicationRefId == applicationRefId
                    && mapping.PermissionRefId == permission.Id
                    && mapping.State == "PUBLISHED")
                .Select(mapping => mapping.RoleRefId)
                .ToListAsync(cancellationToken);
            rolesGrantingPermission = grantingRoleIds
                .Where(roleKeyById.ContainsKey)
                .Select(id => roleKeyById[id])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<PolicyEntity> policies = await dbContext.Policies
                .AsNoTracking()
                .Where(policy => policy.ApplicationRefId == applicationRefId
                    && policy.PermissionRefId == permission.Id
                    && policy.State == "PUBLISHED")
                .ToListAsync(cancellationToken);
            relevantPolicies = policies
                .Select(policy => new PolicyDiagnostic(
                    policy.PolicyKey,
                    policy.Effect,
                    policy.Conditions,
                    Matched: matchedPolicies.Contains(policy.PolicyKey, StringComparer.OrdinalIgnoreCase)))
                .ToList();
            referencedContextAttributes = policies
                .SelectMany(policy => ExtractContextAttributes(policy.Conditions))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // 5 — Typo detection: close known addresses, only when the subject is unknown.
        List<string> similarKnownSubjectEmails = [];
        if (!subjectKnown && !string.IsNullOrWhiteSpace(subjectEmail))
        {
            List<string> knownEmails = await dbContext.Assignments
                .AsNoTracking()
                .Where(assignment => assignment.ApplicationRefId == applicationRefId
                    && assignment.SubjectType == subjectType
                    && assignment.SubjectEmail != null)
                .Select(assignment => assignment.SubjectEmail!)
                .Distinct()
                .ToListAsync(cancellationToken);
            string target = subjectEmail.ToLowerInvariant();
            similarKnownSubjectEmails = knownEmails
                .Select(email => (email, distance: Levenshtein(email.ToLowerInvariant(), target)))
                .Where(candidate => candidate.distance is > 0 and <= SimilarityThreshold)
                .OrderBy(candidate => candidate.distance)
                .Select(candidate => candidate.email)
                .Take(3)
                .ToList();
        }

        return new DecisionDiagnostics(
            PermissionExists: permissionExists,
            ResourceTypeKnown: resourceTypeKnown,
            AvailableActionsForResourceType: availableActionsForResourceType,
            KnownResourceTypes: knownResourceTypes,
            SubjectKnown: subjectKnown,
            ActiveAssignmentCount: activeAssignments.Count,
            RevokedAssignmentCount: revokedAssignmentCount,
            ExpiredAssignmentCount: expiredAssignmentCount,
            PendingAssignmentCount: pendingAssignmentCount,
            SubjectRoleKeys: subjectRoleKeys,
            SimilarKnownSubjectEmails: similarKnownSubjectEmails,
            RolesGrantingPermission: rolesGrantingPermission,
            RelevantPolicies: relevantPolicies,
            ReferencedContextAttributes: referencedContextAttributes,
            ProvidedContextKeys: providedContextKeys);
    }

    // Walks a policy condition document and collects the distinct "context.<name>" attributes it
    // references (both as a leaf attribute and as a "context." expected value). Shared with
    // <see cref="ConfigAdvisorBuilder"/> for the "dead context attribute" lint check.
    internal static IEnumerable<string> ExtractContextAttributes(string conditionsJson)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using JsonDocument document = JsonDocument.Parse(conditionsJson);
            CollectContextAttributes(document.RootElement, names);
        }
        catch (JsonException)
        {
            // A malformed stored policy should not break diagnostics.
        }

        return names;
    }

    private static void CollectContextAttributes(JsonElement element, HashSet<string> names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (element.TryGetProperty("conditions", out JsonElement conditions) && conditions.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in conditions.EnumerateArray())
            {
                CollectContextAttributes(child, names);
            }

            return;
        }

        AddIfContextReference(element, "attribute", names);
        AddIfContextReference(element, "value", names);
    }

    private static void AddIfContextReference(JsonElement element, string propertyName, HashSet<string> names)
    {
        if (element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String)
        {
            string? value = property.GetString();
            if (value is not null && value.StartsWith("context.", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(value["context.".Length..]);
            }
        }
    }

    // Standard iterative Levenshtein edit distance. Inputs are short (email addresses), so the
    // O(n*m) cost is negligible and the set of candidate emails is de-duplicated first.
    private static int Levenshtein(string a, string b)
    {
        if (a == b)
        {
            return 0;
        }

        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
