namespace Authorization.Api.Governance;

/// <summary>
/// Lifecycle status values for governed applications. Values mirror the
/// <c>authz</c> schema check constraints and must not diverge from them.
/// </summary>
public static class GovernanceStatus
{
    public const string Active = "ACTIVE";
    public const string Disabled = "DISABLED";
    public const string Archived = "ARCHIVED";
    public const string Deprecated = "DEPRECATED";
}

/// <summary>
/// Draft/publish workflow states shared by policies, role-permission mappings
/// and assignments. Values mirror the <c>authz</c> schema check constraints.
/// </summary>
public static class WorkflowState
{
    public const string Draft = "DRAFT";
    public const string Published = "PUBLISHED";
    public const string Revoked = "REVOKED";
    public const string Active = "ACTIVE";
}

/// <summary>
/// Risk classification applied to applications, roles and permissions.
/// </summary>
public static class RiskLevel
{
    public const string Low = "LOW";
    public const string Medium = "MEDIUM";
    public const string High = "HIGH";
    public const string Critical = "CRITICAL";
}

/// <summary>
/// Derived display states for an assignment as surfaced to admin tooling.
/// </summary>
public static class AssignmentDisplayStatus
{
    public const string Active = "ACTIVE";
    public const string Expired = "EXPIRED";
    public const string Revoked = "REVOKED";

    /// <summary>
    /// Resolves the effective display state of an assignment from its stored
    /// state, expiry and revocation timestamp.
    /// </summary>
    public static string Resolve(string state, DateTimeOffset? validUntil, DateTimeOffset? revokedAt)
    {
        if (string.Equals(state, Revoked, StringComparison.OrdinalIgnoreCase) || revokedAt is not null)
        {
            return Revoked;
        }

        if (validUntil is not null && validUntil < DateTimeOffset.UtcNow)
        {
            return Expired;
        }

        return Active;
    }
}
