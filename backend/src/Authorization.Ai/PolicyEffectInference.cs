namespace Authorization.Ai;

/// <summary>
/// Infers a policy effect (ALLOW/DENY) from a plain-language instruction. Used as a safety net so a
/// "deny/block/…" instruction is never silently turned into an ALLOW policy when the model omits the
/// structured effect field. The author always confirms the effect before saving.
/// </summary>
internal static class PolicyEffectInference
{
    private static readonly string[] DenyMarkers =
        ["deny", "block", "prevent", "forbid", "reject", "disallow", "restrict", "refuse"];

    /// <summary>Returns "DENY" when the instruction expresses a blocking intent, otherwise "ALLOW".</summary>
    public static string FromInstruction(string instruction)
    {
        string text = (instruction ?? string.Empty).ToLowerInvariant();
        foreach (string marker in DenyMarkers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return "DENY";
            }
        }

        return "ALLOW";
    }
}
