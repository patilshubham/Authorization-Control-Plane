namespace Authorization.Ai;

/// <summary>
/// The operator ids the AI policy-drafting prompt is allowed to emit.
/// <para>
/// The <c>Authorization.Ai</c> project deliberately has no dependency on the runtime
/// engine, so this list is a local mirror of the backend
/// <c>PolicyOperators</c> registry. A parity unit test asserts the two stay identical,
/// so adding an operator in one place fails the build's tests until both are updated.
/// </para>
/// </summary>
public static class AiPolicyOperators
{
    /// <summary>Supported operator ids, mirrored from the runtime registry.</summary>
    public static readonly IReadOnlyList<string> Ids =
    [
        "eq", "neq", "gt", "gte", "lt", "lte",
        "contains", "notContains", "startsWith", "endsWith", "matches", "notMatches",
        "in", "notIn", "containsAny", "containsAll",
        "before", "after", "between",
        "exists", "notExists", "isTrue", "isFalse",
    ];

    /// <summary>Comma-separated operator list for prompt injection.</summary>
    public static string CsvList => string.Join(", ", Ids);
}
