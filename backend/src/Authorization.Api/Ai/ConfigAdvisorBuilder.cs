using System.Globalization;
using System.Text.Json;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Ai;

/// <summary>
/// Runs a fixed catalog of read-only governance checks over an application's authorization
/// configuration (F6). Every finding is deterministic and grounded in a database query, so it is
/// safe to surface without any AI. The AI layer only ranks and explains these findings; it never
/// invents them. The builder never mutates the store.
/// </summary>
public sealed class ConfigAdvisorBuilder
{
    // A DRAFT policy older than this is flagged as stale so it does not linger unnoticed.
    private const int StaleDraftDays = 14;

    // Two roles whose published permission sets overlap by at least this fraction are flagged as
    // near-duplicates (a consolidation hint). Kept high to avoid noisy false positives.
    private const double DuplicateRoleOverlapThreshold = 0.8;

    // How many recent decisions to scan when deciding whether a context attribute is ever supplied.
    private const int DecisionScanLimit = 1000;

    private static readonly IReadOnlyDictionary<string, int> SeverityRank =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["HIGH"] = 0,
            ["MEDIUM"] = 1,
            ["LOW"] = 2,
        };

    private readonly AuthorizationDbContext dbContext;
    private readonly TimeProvider timeProvider;

    public ConfigAdvisorBuilder(AuthorizationDbContext dbContext, TimeProvider timeProvider)
    {
        this.dbContext = dbContext;
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// Runs the full check catalog for the application and returns the findings ordered by
    /// severity (high first) then kind, so the output is stable and reviewer-friendly.
    /// </summary>
    public async Task<IReadOnlyList<ConfigFinding>> BuildAsync(
        Guid applicationRefId,
        string applicationId,
        CancellationToken cancellationToken)
    {
        List<RoleEntity> roles = await dbContext.Roles.AsNoTracking()
            .Where(role => role.ApplicationRefId == applicationRefId && role.Status == "ACTIVE")
            .ToListAsync(cancellationToken);

        List<PermissionEntity> permissions = await dbContext.Permissions.AsNoTracking()
            .Where(permission => permission.ApplicationRefId == applicationRefId && permission.Status == "ACTIVE")
            .ToListAsync(cancellationToken);

        List<RolePermissionEntity> rolePermissions = await dbContext.RolePermissions.AsNoTracking()
            .Where(mapping => mapping.ApplicationRefId == applicationRefId)
            .ToListAsync(cancellationToken);

        List<AssignmentEntity> assignments = await dbContext.Assignments.AsNoTracking()
            .Where(assignment => assignment.ApplicationRefId == applicationRefId)
            .ToListAsync(cancellationToken);

        List<PolicyEntity> policies = await dbContext.Policies.AsNoTracking()
            .Where(policy => policy.ApplicationRefId == applicationRefId)
            .ToListAsync(cancellationToken);

        List<string?> contextSnapshots = await dbContext.Decisions.AsNoTracking()
            .Where(decision => decision.ApplicationId == applicationId)
            .OrderByDescending(decision => decision.Timestamp)
            .Take(DecisionScanLimit)
            .Select(decision => decision.ContextSnapshot)
            .ToListAsync(cancellationToken);

        // Only published mappings actually grant access; drafts are not yet effective.
        var publishedByPermission = rolePermissions
            .Where(mapping => string.Equals(mapping.State, "PUBLISHED", StringComparison.OrdinalIgnoreCase))
            .ToLookup(mapping => mapping.PermissionRefId);
        var publishedByRole = rolePermissions
            .Where(mapping => string.Equals(mapping.State, "PUBLISHED", StringComparison.OrdinalIgnoreCase))
            .ToLookup(mapping => mapping.RoleRefId);

        var findings = new List<ConfigFinding>();
        DateTimeOffset now = timeProvider.GetUtcNow();

        AddOrphanPermissions(findings, permissions, publishedByPermission);
        AddUnusedRoles(findings, roles, assignments);
        AddPrivilegedUnguardedRoles(findings, roles, permissions, publishedByRole, policies);
        AddDeadContextAttributes(findings, policies, contextSnapshots);
        AddStaleDrafts(findings, policies, now);
        AddUnfireableRules(findings, policies);
        AddNearDuplicateRoles(findings, roles, permissions, publishedByRole);

        return findings
            .OrderBy(finding => SeverityRank.TryGetValue(finding.Severity, out int rank) ? rank : int.MaxValue)
            .ThenBy(finding => finding.Kind, StringComparer.Ordinal)
            .ThenBy(finding => finding.Id, StringComparer.Ordinal)
            .ToList();
    }

    // ORPHAN_PERMISSION — an active permission that no published role-permission mapping grants.
    private static void AddOrphanPermissions(
        List<ConfigFinding> findings,
        List<PermissionEntity> permissions,
        ILookup<Guid, RolePermissionEntity> publishedByPermission)
    {
        foreach (PermissionEntity permission in permissions)
        {
            if (publishedByPermission[permission.Id].Any())
            {
                continue;
            }

            findings.Add(new ConfigFinding(
                Id: $"orphan-permission:{permission.PermissionKey}",
                Kind: "ORPHAN_PERMISSION",
                Severity: "MEDIUM",
                Title: $"Permission '{permission.PermissionKey}' is not granted by any role",
                Detail: "No published role grants this permission, so it can never be exercised. Grant it to a role or retire it.",
                EntityType: "PERMISSION",
                EntityKey: permission.PermissionKey));
        }
    }

    // UNUSED_ROLE — an active role that no active assignment references.
    private static void AddUnusedRoles(
        List<ConfigFinding> findings,
        List<RoleEntity> roles,
        List<AssignmentEntity> assignments)
    {
        var activelyAssignedRoleIds = assignments
            .Where(assignment => string.Equals(assignment.State, "ACTIVE", StringComparison.OrdinalIgnoreCase)
                && assignment.RevokedAt is null)
            .Select(assignment => assignment.RoleRefId)
            .ToHashSet();

        foreach (RoleEntity role in roles)
        {
            if (activelyAssignedRoleIds.Contains(role.Id))
            {
                continue;
            }

            findings.Add(new ConfigFinding(
                Id: $"unused-role:{role.RoleKey}",
                Kind: "UNUSED_ROLE",
                Severity: role.Privileged ? "MEDIUM" : "LOW",
                Title: $"Role '{role.RoleKey}' has no active assignments",
                Detail: role.Privileged
                    ? "This privileged role is assigned to no active subject. Assign it where needed or archive it to reduce the attack surface."
                    : "This role is assigned to no active subject. Assign it where needed or archive it.",
                EntityType: "ROLE",
                EntityKey: role.RoleKey));
        }
    }

    // PRIVILEGED_UNGUARDED — a privileged (or high-risk) role that grants permissions, none of
    // which is constrained by any policy.
    private static void AddPrivilegedUnguardedRoles(
        List<ConfigFinding> findings,
        List<RoleEntity> roles,
        List<PermissionEntity> permissions,
        ILookup<Guid, RolePermissionEntity> publishedByRole,
        List<PolicyEntity> policies)
    {
        var permissionExists = permissions.Select(permission => permission.Id).ToHashSet();
        var policiedPermissionIds = policies
            .Select(policy => policy.PermissionRefId)
            .ToHashSet();

        foreach (RoleEntity role in roles)
        {
            bool highRisk = role.Privileged
                || string.Equals(role.RiskLevel, "HIGH", StringComparison.OrdinalIgnoreCase)
                || string.Equals(role.RiskLevel, "CRITICAL", StringComparison.OrdinalIgnoreCase);
            if (!highRisk)
            {
                continue;
            }

            List<Guid> grantedPermissionIds = publishedByRole[role.Id]
                .Select(mapping => mapping.PermissionRefId)
                .Where(permissionExists.Contains)
                .Distinct()
                .ToList();

            // A role that grants nothing carries no unguarded access, so it is not flagged here.
            if (grantedPermissionIds.Count == 0 || grantedPermissionIds.Any(policiedPermissionIds.Contains))
            {
                continue;
            }

            findings.Add(new ConfigFinding(
                Id: $"privileged-unguarded:{role.RoleKey}",
                Kind: "PRIVILEGED_UNGUARDED",
                Severity: "HIGH",
                Title: $"Privileged role '{role.RoleKey}' has no guarding policy",
                Detail: $"This role grants {grantedPermissionIds.Count} permission(s), none constrained by a policy. Add a policy to limit when this privileged access applies.",
                EntityType: "ROLE",
                EntityKey: role.RoleKey));
        }
    }

    // DEAD_CONTEXT_ATTRIBUTE — a published policy references a context.<x> attribute that has never
    // appeared in any recorded decision's context snapshot. Only evaluated when decisions exist, so
    // an application with no traffic yet is never falsely flagged.
    private static void AddDeadContextAttributes(
        List<ConfigFinding> findings,
        List<PolicyEntity> policies,
        List<string?> contextSnapshots)
    {
        var seenContextKeys = CollectSeenContextKeys(contextSnapshots);
        if (seenContextKeys is null)
        {
            // No decisions recorded at all — we cannot conclude an attribute is dead.
            return;
        }

        foreach (PolicyEntity policy in policies
            .Where(policy => string.Equals(policy.State, "PUBLISHED", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (string attribute in DecisionDiagnosticsBuilder
                .ExtractContextAttributes(policy.Conditions)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                if (seenContextKeys.Contains(attribute))
                {
                    continue;
                }

                findings.Add(new ConfigFinding(
                    Id: $"dead-context-attribute:{policy.PolicyKey}:{attribute}",
                    Kind: "DEAD_CONTEXT_ATTRIBUTE",
                    Severity: "LOW",
                    Title: $"Policy '{policy.PolicyKey}' references context attribute '{attribute}' never seen in decisions",
                    Detail: $"No recorded decision has supplied context.{attribute}, so this condition may never evaluate as intended. Confirm callers send it, or simplify the policy.",
                    EntityType: "POLICY",
                    EntityKey: policy.PolicyKey));
            }
        }
    }

    // STALE_DRAFT — a policy left in DRAFT for longer than the staleness threshold.
    private void AddStaleDrafts(List<ConfigFinding> findings, List<PolicyEntity> policies, DateTimeOffset now)
    {
        DateTimeOffset cutoff = now.AddDays(-StaleDraftDays);
        foreach (PolicyEntity policy in policies
            .Where(policy => string.Equals(policy.State, "DRAFT", StringComparison.OrdinalIgnoreCase)
                && policy.CreatedAt < cutoff))
        {
            int days = (int)(now - policy.CreatedAt).TotalDays;
            findings.Add(new ConfigFinding(
                Id: $"stale-draft:{policy.PolicyKey}",
                Kind: "STALE_DRAFT",
                Severity: "LOW",
                Title: $"Draft policy '{policy.PolicyKey}' has been unpublished for {days} days",
                Detail: $"This policy has been in DRAFT for {days} days. Publish it if it is ready, or delete it to avoid confusion.",
                EntityType: "POLICY",
                EntityKey: policy.PolicyKey));
        }
    }

    // UNFIREABLE_RULE — a policy whose AND-ed conditions contradict each other and can never match.
    private static void AddUnfireableRules(List<ConfigFinding> findings, List<PolicyEntity> policies)
    {
        foreach (PolicyEntity policy in policies)
        {
            if (!IsUnfireable(policy.Conditions))
            {
                continue;
            }

            findings.Add(new ConfigFinding(
                Id: $"unfireable-rule:{policy.PolicyKey}",
                Kind: "UNFIREABLE_RULE",
                Severity: "MEDIUM",
                Title: $"Policy '{policy.PolicyKey}' can never match",
                Detail: $"The conditions on this {policy.Effect} policy contradict each other, so the rule can never take effect. Review and correct the conditions.",
                EntityType: "POLICY",
                EntityKey: policy.PolicyKey));
        }
    }

    // DUPLICATE_ROLES — pairs of roles whose published permission sets overlap above the threshold.
    private static void AddNearDuplicateRoles(
        List<ConfigFinding> findings,
        List<RoleEntity> roles,
        List<PermissionEntity> permissions,
        ILookup<Guid, RolePermissionEntity> publishedByRole)
    {
        var permissionExists = permissions.Select(permission => permission.Id).ToHashSet();

        // Order roles by key for deterministic, de-duplicated pair enumeration.
        List<RoleEntity> ordered = roles.OrderBy(role => role.RoleKey, StringComparer.Ordinal).ToList();
        var permissionSets = ordered.ToDictionary(
            role => role.Id,
            role => publishedByRole[role.Id]
                .Select(mapping => mapping.PermissionRefId)
                .Where(permissionExists.Contains)
                .ToHashSet());

        for (int i = 0; i < ordered.Count; i++)
        {
            HashSet<Guid> first = permissionSets[ordered[i].Id];
            if (first.Count == 0)
            {
                continue;
            }

            for (int j = i + 1; j < ordered.Count; j++)
            {
                HashSet<Guid> second = permissionSets[ordered[j].Id];
                if (second.Count == 0)
                {
                    continue;
                }

                int intersection = first.Count(second.Contains);
                int union = first.Count + second.Count - intersection;
                double overlap = union == 0 ? 0 : (double)intersection / union;
                if (overlap < DuplicateRoleOverlapThreshold)
                {
                    continue;
                }

                int percent = (int)Math.Round(overlap * 100);
                findings.Add(new ConfigFinding(
                    Id: $"duplicate-roles:{ordered[i].RoleKey}:{ordered[j].RoleKey}",
                    Kind: "DUPLICATE_ROLES",
                    Severity: "LOW",
                    Title: $"Roles '{ordered[i].RoleKey}' and '{ordered[j].RoleKey}' grant nearly the same permissions",
                    Detail: $"These roles share {percent}% of their permissions. Consider consolidating them into one to simplify administration.",
                    EntityType: "ROLE",
                    EntityKey: ordered[i].RoleKey));
            }
        }
    }

    // Returns the distinct set of top-level context keys ever supplied across the scanned decision
    // snapshots, or <c>null</c> when there are no decisions to reason about.
    private static HashSet<string>? CollectSeenContextKeys(List<string?> contextSnapshots)
    {
        if (contextSnapshots.Count == 0)
        {
            return null;
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? snapshot in contextSnapshots)
        {
            if (string.IsNullOrWhiteSpace(snapshot))
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(snapshot);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in document.RootElement.EnumerateObject())
                    {
                        keys.Add(property.Name);
                    }
                }
            }
            catch (JsonException)
            {
                // A malformed snapshot should not break the advisor.
            }
        }

        return keys;
    }

    // A conservative "can never match" check: walks AND (all) groups and reports a contradiction
    // among their literal leaf conditions on the same attribute. "any" (OR) groups are not
    // descended into, so the check never produces a false positive on satisfiable rules.
    private static bool IsUnfireable(string conditionsJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(conditionsJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("conditions", out JsonElement conditions)
                || conditions.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            string match = root.TryGetProperty("match", out JsonElement matchElement)
                && matchElement.ValueKind == JsonValueKind.String
                    ? matchElement.GetString() ?? "all"
                    : "all";
            return GroupIsUnfireable(match, conditions);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool GroupIsUnfireable(string match, JsonElement conditions)
    {
        // Only AND groups can be made unfireable by contradictory leaves; be conservative for OR.
        if (string.Equals(match, "any", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var leaves = new List<Leaf>();
        foreach (JsonElement node in conditions.EnumerateArray())
        {
            if (node.TryGetProperty("conditions", out JsonElement childConditions)
                && childConditions.ValueKind == JsonValueKind.Array)
            {
                string childMatch = node.TryGetProperty("match", out JsonElement childMatchElement)
                    && childMatchElement.ValueKind == JsonValueKind.String
                        ? childMatchElement.GetString() ?? "all"
                        : "all";
                if (GroupIsUnfireable(childMatch, childConditions))
                {
                    return true;
                }

                continue;
            }

            Leaf? leaf = ParseLeaf(node);
            if (leaf is not null)
            {
                leaves.Add(leaf);
            }
        }

        return LeavesContradict(leaves);
    }

    private static Leaf? ParseLeaf(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object
            || !node.TryGetProperty("attribute", out JsonElement attributeElement)
            || attributeElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string attribute = attributeElement.GetString() ?? string.Empty;
        string op = node.TryGetProperty("operator", out JsonElement operatorElement)
            && operatorElement.ValueKind == JsonValueKind.String
                ? operatorElement.GetString() ?? string.Empty
                : string.Empty;

        string? value = null;
        if (node.TryGetProperty("value", out JsonElement valueElement) && valueElement.ValueKind == JsonValueKind.String)
        {
            value = valueElement.GetString();
        }

        // Only literal comparisons are analysable; a value that resolves from context/assignment is
        // dynamic and cannot be judged contradictory at rest.
        bool isLiteral = value is not null
            && !value.StartsWith("context.", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("assignment.", StringComparison.OrdinalIgnoreCase);

        return new Leaf(attribute, op, value, isLiteral);
    }

    private static bool LeavesContradict(List<Leaf> leaves)
    {
        foreach (IGrouping<string, Leaf> group in leaves.GroupBy(leaf => leaf.Attribute, StringComparer.OrdinalIgnoreCase))
        {
            if (AttributeLeavesContradict(group.ToList()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AttributeLeavesContradict(List<Leaf> leaves)
    {
        bool hasExists = leaves.Any(leaf => leaf.Operator == "exists");
        bool hasNotExists = leaves.Any(leaf => leaf.Operator == "notExists");
        if (hasExists && hasNotExists)
        {
            return true;
        }

        List<string> eqValues = leaves
            .Where(leaf => leaf.Operator == "eq" && leaf.IsLiteral)
            .Select(leaf => leaf.Value!)
            .ToList();

        // Two different required-equal values can never both hold.
        if (eqValues.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        {
            return true;
        }

        if (eqValues.Count > 0)
        {
            string required = eqValues[0];
            bool requiredIsEmpty = string.IsNullOrEmpty(required);

            // eq X together with any of these on the same attribute is impossible.
            if (leaves.Any(leaf => leaf.Operator == "neq" && leaf.IsLiteral
                    && string.Equals(leaf.Value, required, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (!requiredIsEmpty && hasNotExists)
            {
                return true;
            }

            if (leaves.Any(leaf => leaf.Operator == "in" && leaf.IsLiteral
                    && !SplitList(leaf.Value!).Contains(required, StringComparer.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (leaves.Any(leaf => leaf.Operator == "notIn" && leaf.IsLiteral
                    && SplitList(leaf.Value!).Contains(required, StringComparer.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return NumericBoundsContradict(leaves);
    }

    // Detects an empty numeric window, e.g. (gt 100) AND (lt 50), or (gte 10) AND (lte 10) combined
    // with a strict bound.
    private static bool NumericBoundsContradict(List<Leaf> leaves)
    {
        decimal? lower = null;
        bool lowerStrict = false;
        decimal? upper = null;
        bool upperStrict = false;

        foreach (Leaf leaf in leaves.Where(leaf => leaf.IsLiteral))
        {
            if (!decimal.TryParse(leaf.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number))
            {
                continue;
            }

            switch (leaf.Operator)
            {
                case "gt":
                    if (lower is null || number >= lower) { lower = number; lowerStrict = true; }
                    break;
                case "gte":
                    if (lower is null || number > lower) { lower = number; lowerStrict = false; }
                    break;
                case "lt":
                    if (upper is null || number <= upper) { upper = number; upperStrict = true; }
                    break;
                case "lte":
                    if (upper is null || number < upper) { upper = number; upperStrict = false; }
                    break;
            }
        }

        if (lower is null || upper is null)
        {
            return false;
        }

        if (lower > upper)
        {
            return true;
        }

        return lower == upper && (lowerStrict || upperStrict);
    }

    private static string[] SplitList(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private sealed record Leaf(string Attribute, string Operator, string? Value, bool IsLiteral);
}

/// <summary>
/// A single deterministic governance finding. <see cref="Id"/> is stable across runs so the AI
/// layer can attach a suggested fix to it without ambiguity.
/// </summary>
public sealed record ConfigFinding(
    string Id,
    string Kind,
    string Severity,
    string Title,
    string Detail,
    string? EntityType,
    string? EntityKey);
