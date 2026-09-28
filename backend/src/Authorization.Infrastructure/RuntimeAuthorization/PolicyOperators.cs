namespace Authorization.Infrastructure.RuntimeAuthorization;

/// <summary>
/// Number of values a leaf condition's operator consumes.
/// </summary>
public enum OperatorArity
{
    /// <summary>No value (presence / boolean operators).</summary>
    None,

    /// <summary>A single scalar value.</summary>
    Single,

    /// <summary>A single numeric value.</summary>
    Number,

    /// <summary>A comma-separated (or JSON array) list of values.</summary>
    List,

    /// <summary>Exactly two values (a lower and upper bound), comma-separated.</summary>
    Range,
}

/// <summary>
/// The typed comparison family an operator belongs to. Drives value-input hints
/// on the client and documents evaluation semantics.
/// </summary>
public enum OperatorValueType
{
    Any,
    Number,
    DateTime,
    Boolean,
    Regex,
    Text,
}

/// <summary>Metadata describing a single policy leaf operator.</summary>
public sealed record PolicyOperatorDefinition(
    string Id,
    OperatorArity Arity,
    OperatorValueType ValueType,
    string Group,
    string Label);

/// <summary>
/// The single backend source of truth for the operators a policy leaf condition
/// may use, and for the group-combining modes a group node may declare.
/// <para>
/// The runtime engine (<see cref="EfAuthorizationPolicyEngine"/>), the API-side
/// condition validator, and the AI policy-drafting prompt all derive their
/// supported-operator sets from this type so they can never drift apart.
/// A parity test keeps the frontend operator model aligned as well.
/// </para>
/// </summary>
public static class PolicyOperators
{
    /// <summary>Group node combines children with logical AND.</summary>
    public const string MatchAll = "all";

    /// <summary>Group node combines children with logical OR.</summary>
    public const string MatchAny = "any";

    /// <summary>Group node negates its children (NOT any child matches).</summary>
    public const string MatchNone = "none";

    /// <summary>The complete, ordered set of supported operators with metadata.</summary>
    public static readonly IReadOnlyList<PolicyOperatorDefinition> All =
    [
        // ── Comparison (typed: numeric / date / ordinal fallback) ──
        new("eq", OperatorArity.Single, OperatorValueType.Any, "compare", "equals"),
        new("neq", OperatorArity.Single, OperatorValueType.Any, "compare", "does not equal"),
        new("gt", OperatorArity.Number, OperatorValueType.Number, "compare", "greater than"),
        new("gte", OperatorArity.Number, OperatorValueType.Number, "compare", "greater than or equal"),
        new("lt", OperatorArity.Number, OperatorValueType.Number, "compare", "less than"),
        new("lte", OperatorArity.Number, OperatorValueType.Number, "compare", "less than or equal"),

        // ── Text ──
        new("contains", OperatorArity.Single, OperatorValueType.Text, "text", "contains"),
        new("notContains", OperatorArity.Single, OperatorValueType.Text, "text", "does not contain"),
        new("startsWith", OperatorArity.Single, OperatorValueType.Text, "text", "starts with"),
        new("endsWith", OperatorArity.Single, OperatorValueType.Text, "text", "ends with"),
        new("matches", OperatorArity.Single, OperatorValueType.Regex, "text", "matches regex"),
        new("notMatches", OperatorArity.Single, OperatorValueType.Regex, "text", "does not match regex"),

        // ── Set / collection ──
        new("in", OperatorArity.List, OperatorValueType.Any, "set", "in list"),
        new("notIn", OperatorArity.List, OperatorValueType.Any, "set", "not in list"),
        new("containsAny", OperatorArity.List, OperatorValueType.Any, "set", "contains any of"),
        new("containsAll", OperatorArity.List, OperatorValueType.Any, "set", "contains all of"),

        // ── Date / range ──
        new("before", OperatorArity.Single, OperatorValueType.DateTime, "datetime", "before (date)"),
        new("after", OperatorArity.Single, OperatorValueType.DateTime, "datetime", "after (date)"),
        new("between", OperatorArity.Range, OperatorValueType.Any, "datetime", "between (inclusive)"),

        // ── Presence / boolean ──
        new("exists", OperatorArity.None, OperatorValueType.Any, "presence", "is present"),
        new("notExists", OperatorArity.None, OperatorValueType.Any, "presence", "is absent"),
        new("isTrue", OperatorArity.None, OperatorValueType.Boolean, "presence", "is true"),
        new("isFalse", OperatorArity.None, OperatorValueType.Boolean, "presence", "is false"),
    ];

    /// <summary>Case-sensitive set of supported operator ids.</summary>
    public static readonly IReadOnlySet<string> Ids =
        All.Select(operatorDefinition => operatorDefinition.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>Supported group-combining modes.</summary>
    public static readonly IReadOnlySet<string> MatchModes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MatchAll, MatchAny, MatchNone };

    /// <summary>True when <paramref name="id"/> is a supported operator.</summary>
    public static bool IsSupported(string? id) => id is not null && Ids.Contains(id);

    /// <summary>True when <paramref name="match"/> is a supported group-combining mode.</summary>
    public static bool IsSupportedMatch(string? match) => match is not null && MatchModes.Contains(match);

    /// <summary>Comma-separated operator list, used to build AI prompts and diagnostics.</summary>
    public static string CsvList => string.Join(", ", All.Select(operatorDefinition => operatorDefinition.Id));
}
