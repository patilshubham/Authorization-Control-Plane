using Authorization.Ai;
using Authorization.Api.Ai;

namespace Authorization.Api.Tests.Ai;

/// <summary>
/// Tests for the deterministic guidance builder that turns a failed / low-confidence access-search
/// question into helpful, capability-aware suggestions. Every emitted suggestion must be phrased in
/// the shape the planner understands and grounded only in the vocabulary that was supplied — the
/// suggester never invents keys or fields that don't exist.
/// </summary>
public sealed class AccessSearchSuggesterTests
{
    private static AccessSearchVocabulary Vocabulary(
        IReadOnlyList<string>? permissions = null,
        IReadOnlyList<string>? resources = null,
        IReadOnlyList<string>? actions = null,
        IReadOnlyList<string>? roles = null,
        IReadOnlyList<string>? resourceIds = null) =>
        new(
            permissions ?? ["Finance.Read"],
            resources ?? ["prices"],
            actions ?? ["publish"],
            roles ?? ["pricing-admin"],
            resourceIds ?? []);

    [Fact]
    public void Build_AlwaysReturnsBetweenTwoAndFiveGroundedSuggestions()
    {
        AccessSearchGuidance guidance = AccessSearchSuggester.Build(
            "tell me something",
            Vocabulary(),
            "reason");

        Assert.InRange(guidance.Suggestions.Count, 2, 5);
        Assert.Equal("reason", guidance.Reason);
    }

    [Fact]
    public void Build_ExpiryQuestion_SuggestsAnswerableAssignmentStateQuery()
    {
        AccessSearchGuidance guidance = AccessSearchSuggester.Build(
            "How many users are past expiry date?",
            Vocabulary(),
            "reason");

        Assert.Contains(
            guidance.Suggestions,
            s => s.Contains("EXPIRED", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_InactiveRolesQuestion_SteersToSupportedRoleQueries()
    {
        AccessSearchGuidance guidance = AccessSearchSuggester.Build(
            "How many roles are inactive?",
            Vocabulary(),
            "reason");

        // Roles now carry a status field, so the guidance steers to the real, answerable status filter.
        Assert.Contains(
            guidance.Suggestions,
            s => s.Contains("status", StringComparison.OrdinalIgnoreCase)
                || s.Contains("ACTIVE", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(guidance.Intents);
    }

    [Fact]
    public void Build_WithAiSuggestions_PrefersThemVerbatim()
    {
        AccessSearchGuidance guidance = AccessSearchSuggester.Build(
            "something the model could not map",
            Vocabulary(),
            "reason",
            ["Which roles are in status DISABLED?", "How many assignments are in state EXPIRED?"]);

        Assert.Contains("Which roles are in status DISABLED?", guidance.Suggestions);
        Assert.Contains("How many assignments are in state EXPIRED?", guidance.Suggestions);
        Assert.InRange(guidance.Suggestions.Count, 2, 5);
    }

    [Fact]
    public void Build_GroundsPromptsInSuppliedVocabulary()
    {
        AccessSearchGuidance guidance = AccessSearchSuggester.Build(
            "who has access to reports?",
            Vocabulary(permissions: ["Reports.View"], roles: ["report-viewer"]),
            "reason");

        // Concrete prompts reference the real keys, never invented ones.
        Assert.Contains(
            guidance.Suggestions,
            s => s.Contains("Reports.View", StringComparison.Ordinal)
                || s.Contains("report-viewer", StringComparison.Ordinal));
        Assert.DoesNotContain(
            guidance.Suggestions,
            s => s.Contains("Finance.Read", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WithEmptyVocabulary_StillReturnsUsableSuggestions()
    {
        AccessSearchGuidance guidance = AccessSearchSuggester.Build(
            "asdf qwerty",
            Vocabulary(permissions: [], resources: [], actions: [], roles: []),
            "reason");

        Assert.InRange(guidance.Suggestions.Count, 2, 5);
        // No suggestion should contain an empty placeholder from a missing vocabulary value.
        Assert.DoesNotContain(guidance.Suggestions, s => s.Contains("  "));
    }

    [Fact]
    public void Build_CapsSuggestionsAtFive()
    {
        AccessSearchGuidance guidance = AccessSearchSuggester.Build(
            "how many users, roles, permissions and policies are inactive or expired?",
            Vocabulary(),
            "reason");

        Assert.True(guidance.Suggestions.Count <= 5);
        Assert.True(guidance.Intents.Count <= 3);
    }
}
