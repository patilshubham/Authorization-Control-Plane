using System.Text.Json;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Ai;

/// <summary>
/// F7 — deterministically evaluates Separation-of-Duties rules against the structured access model.
/// Detection is entirely code-driven and reproducible: for each active rule it finds every role whose
/// PUBLISHED grants satisfy <b>both</b> permission matchers, and every subject whose <i>combined</i>
/// active assignments satisfy both matchers. The AI layer only ever <i>drafts</i> a rule's matcher pair
/// from natural language; it never decides violations. Read-only: no query mutates state.
/// </summary>
public sealed class SodAnalysisBuilder
{
    private const int MaxViolations = 200;

    private static readonly IReadOnlyDictionary<string, int> SeverityRank =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["CRITICAL"] = 0,
            ["HIGH"] = 1,
            ["MEDIUM"] = 2,
            ["LOW"] = 3,
        };

    private readonly AuthorizationDbContext dbContext;

    public SodAnalysisBuilder(AuthorizationDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    /// <summary>
    /// Evaluate every active SoD rule for the application and return the violations, ordered by
    /// severity then rule then subject/role. Returns an empty list when there are no active rules.
    /// </summary>
    public async Task<IReadOnlyList<SodViolation>> BuildViolationsAsync(
        Guid applicationRefId,
        string applicationId,
        CancellationToken cancellationToken)
    {
        List<SodRuleEntity> rules = await dbContext.SodRules.AsNoTracking()
            .Where(rule => rule.ApplicationRefId == applicationRefId && rule.Status == "ACTIVE")
            .ToListAsync(cancellationToken);

        if (rules.Count == 0)
        {
            return [];
        }

        // Permission id -> (resource, action, key), and the set of permission ids each role PUBLISHES.
        List<PermissionEntity> permissions = await dbContext.Permissions.AsNoTracking()
            .Where(permission => permission.ApplicationRefId == applicationRefId && permission.Status == "ACTIVE")
            .ToListAsync(cancellationToken);

        List<RolePermissionEntity> grants = await dbContext.RolePermissions.AsNoTracking()
            .Where(grant => grant.ApplicationRefId == applicationRefId && grant.State == "PUBLISHED")
            .ToListAsync(cancellationToken);

        List<RoleEntity> roles = await dbContext.Roles.AsNoTracking()
            .Where(role => role.ApplicationRefId == applicationRefId && role.Status == "ACTIVE")
            .ToListAsync(cancellationToken);

        List<AssignmentEntity> assignments = await dbContext.Assignments.AsNoTracking()
            .Where(assignment => assignment.ApplicationRefId == applicationRefId
                && assignment.State == "ACTIVE"
                && assignment.SubjectEmail != null)
            .ToListAsync(cancellationToken);

        var permissionById = permissions.ToDictionary(permission => permission.Id);
        var roleByKey = roles.ToDictionary(role => role.Id, role => role.RoleKey);

        // The permissions each role effectively grants (only ACTIVE permissions that are PUBLISHED to it).
        var permissionsByRole = grants
            .Where(grant => permissionById.ContainsKey(grant.PermissionRefId))
            .GroupBy(grant => grant.RoleRefId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(grant => permissionById[grant.PermissionRefId]).ToList());

        var violations = new List<SodViolation>();

        foreach (SodRuleEntity rule in rules)
        {
            SodMatcher? matcherA = SodMatcher.Parse(rule.MatcherA);
            SodMatcher? matcherB = SodMatcher.Parse(rule.MatcherB);
            if (matcherA is null || matcherB is null)
            {
                continue;
            }

            AddRoleViolations(violations, rule, matcherA, matcherB, permissionsByRole, roleByKey, applicationId);
            AddSubjectViolations(violations, rule, matcherA, matcherB, permissionsByRole, roleByKey, assignments, applicationId);
        }

        return violations
            .OrderBy(violation => SeverityRank.TryGetValue(violation.Severity, out int rank) ? rank : int.MaxValue)
            .ThenBy(violation => violation.RuleKey, StringComparer.Ordinal)
            .ThenBy(violation => violation.Scope, StringComparer.Ordinal)
            .ThenBy(violation => violation.SubjectKey, StringComparer.Ordinal)
            .Take(MaxViolations)
            .ToList();
    }

    private static void AddRoleViolations(
        List<SodViolation> violations,
        SodRuleEntity rule,
        SodMatcher matcherA,
        SodMatcher matcherB,
        IReadOnlyDictionary<Guid, List<PermissionEntity>> permissionsByRole,
        IReadOnlyDictionary<Guid, string> roleByKey,
        string applicationId)
    {
        foreach ((Guid roleId, List<PermissionEntity> rolePermissions) in permissionsByRole)
        {
            List<PermissionEntity> matchedA = rolePermissions.Where(matcherA.Matches).ToList();
            List<PermissionEntity> matchedB = rolePermissions.Where(matcherB.Matches).ToList();
            if (matchedA.Count == 0 || matchedB.Count == 0)
            {
                continue;
            }

            string roleKey = roleByKey.TryGetValue(roleId, out string? key) ? key : "(unknown role)";
            violations.Add(new SodViolation(
                ApplicationId: applicationId,
                RuleKey: rule.RuleKey,
                RuleName: rule.Name,
                Severity: rule.Severity,
                Rationale: rule.Rationale,
                Scope: "ROLE",
                SubjectKey: roleKey,
                SubjectLabel: roleKey,
                ConflictingPermissions: ConflictingKeys(matchedA, matchedB),
                Detail: $"Role '{roleKey}' grants both sides of this rule directly.",
                DeepLinkKind: "role",
                DeepLinkKey: roleKey));
        }
    }

    private static void AddSubjectViolations(
        List<SodViolation> violations,
        SodRuleEntity rule,
        SodMatcher matcherA,
        SodMatcher matcherB,
        IReadOnlyDictionary<Guid, List<PermissionEntity>> permissionsByRole,
        IReadOnlyDictionary<Guid, string> roleByKey,
        IReadOnlyList<AssignmentEntity> assignments,
        string applicationId)
    {
        IEnumerable<IGrouping<string, AssignmentEntity>> bySubject = assignments
            .Where(assignment => assignment.SubjectEmail is not null)
            .GroupBy(assignment => assignment.SubjectEmail!, StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<string, AssignmentEntity> subject in bySubject)
        {
            // The subject's combined effective permissions across all their active role assignments.
            var effective = new List<PermissionEntity>();
            var roleKeys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AssignmentEntity assignment in subject)
            {
                if (permissionsByRole.TryGetValue(assignment.RoleRefId, out List<PermissionEntity>? perms))
                {
                    effective.AddRange(perms);
                }

                if (roleByKey.TryGetValue(assignment.RoleRefId, out string? roleKey))
                {
                    roleKeys.Add(roleKey);
                }
            }

            List<PermissionEntity> matchedA = effective.Where(matcherA.Matches).ToList();
            List<PermissionEntity> matchedB = effective.Where(matcherB.Matches).ToList();
            if (matchedA.Count == 0 || matchedB.Count == 0)
            {
                continue;
            }

            // Only report a subject-level violation when the toxic combination spans more than one role;
            // a single role holding both sides is already reported as a ROLE violation.
            bool withinSingleRole = permissionsByRole.Values.Any(perms =>
                perms.Any(matcherA.Matches) && perms.Any(matcherB.Matches)
                && matchedA.All(perms.Contains) && matchedB.All(perms.Contains));
            if (roleKeys.Count <= 1 && withinSingleRole)
            {
                continue;
            }

            violations.Add(new SodViolation(
                ApplicationId: applicationId,
                RuleKey: rule.RuleKey,
                RuleName: rule.Name,
                Severity: rule.Severity,
                Rationale: rule.Rationale,
                Scope: "SUBJECT",
                SubjectKey: subject.Key,
                SubjectLabel: subject.Key,
                ConflictingPermissions: ConflictingKeys(matchedA, matchedB),
                Detail: $"Combined access via {string.Join(", ", roleKeys)} grants both sides of this rule.",
                DeepLinkKind: "user",
                DeepLinkKey: subject.Key));
        }
    }

    private static IReadOnlyList<string> ConflictingKeys(
        IReadOnlyList<PermissionEntity> matchedA,
        IReadOnlyList<PermissionEntity> matchedB) =>
        matchedA.Concat(matchedB)
            .Select(permission => permission.PermissionKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
}

/// <summary>A parsed SoD permission matcher (permissionKey, or resource/action pair).</summary>
public sealed record SodMatcher(string? PermissionKey, string? Resource, string? Action)
{
    /// <summary>Parse a matcher from its JSON form; returns null when it is empty or malformed.</summary>
    public static SodMatcher? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? permissionKey = ReadTrimmed(root, "permissionKey");
            string? resource = ReadTrimmed(root, "resource");
            string? action = ReadTrimmed(root, "action");

            if (permissionKey is null && resource is null && action is null)
            {
                return null;
            }

            return new SodMatcher(permissionKey, resource, action);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when the permission satisfies this matcher (case-insensitive).</summary>
    public bool Matches(PermissionEntity permission)
    {
        if (PermissionKey is not null)
        {
            return string.Equals(permission.PermissionKey, PermissionKey, StringComparison.OrdinalIgnoreCase);
        }

        bool resourceOk = Resource is null || string.Equals(permission.Resource, Resource, StringComparison.OrdinalIgnoreCase);
        bool actionOk = Action is null || string.Equals(permission.Action, Action, StringComparison.OrdinalIgnoreCase);
        return resourceOk && actionOk;
    }

    private static string? ReadTrimmed(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString()?.Trim() ?? string.Empty;
            return text.Length == 0 ? null : text;
        }

        return null;
    }
}

/// <summary>A deterministic Separation-of-Duties violation with a deep-link citation.</summary>
public sealed record SodViolation(
    string ApplicationId,
    string RuleKey,
    string RuleName,
    string Severity,
    string? Rationale,
    string Scope,
    string SubjectKey,
    string SubjectLabel,
    IReadOnlyList<string> ConflictingPermissions,
    string Detail,
    string? DeepLinkKind,
    string? DeepLinkKey);
