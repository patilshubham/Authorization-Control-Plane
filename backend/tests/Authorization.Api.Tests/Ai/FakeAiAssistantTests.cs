using System.Text.Json;
using Authorization.Ai;

namespace Authorization.Api.Tests.Ai;

public sealed class FakeAiAssistantTests
{
    private readonly FakeAiAssistant assistant = new();

    [Fact]
    public async Task DraftPolicyAsync_ReturnsValidJson_AndWarns()
    {
        var request = new PolicyDraftRequest(
            ApplicationId: "app-1",
            Instruction: "Allow when the amount is under 5000",
            KnownPermissionKeys: ["invoice.read"],
            KnownAttributes: ["context.amount"]);

        PolicyDraftResult result = await assistant.DraftPolicyAsync(request, CancellationToken.None);

        using JsonDocument doc = JsonDocument.Parse(result.ConditionsJson);
        Assert.Equal("all", doc.RootElement.GetProperty("match").GetString());
        Assert.True(doc.RootElement.TryGetProperty("conditions", out JsonElement conditions));
        Assert.Equal(JsonValueKind.Array, conditions.ValueKind);
        Assert.NotEmpty(result.Warnings);
        Assert.Equal("ALLOW", result.SuggestedEffect);
    }

    [Theory]
    [InlineData("Deny if the country is Pakistan", "DENY")]
    [InlineData("Block access when region is CN", "DENY")]
    [InlineData("Prevent publishing outside business hours", "DENY")]
    [InlineData("Allow when the amount is under 5000", "ALLOW")]
    [InlineData("Grant access to EU users", "ALLOW")]
    public async Task DraftPolicyAsync_InfersEffectFromInstruction(string instruction, string expectedEffect)
    {
        var request = new PolicyDraftRequest(
            ApplicationId: "app-1",
            Instruction: instruction,
            KnownPermissionKeys: [],
            KnownAttributes: []);

        PolicyDraftResult result = await assistant.DraftPolicyAsync(request, CancellationToken.None);

        Assert.Equal(expectedEffect, result.SuggestedEffect);
    }

    [Fact]
    public async Task DraftSodRuleAsync_GroundsTwoActions_FromVocabulary()
    {
        var request = new SodRuleDraftRequest(
            ApplicationId: "pricing-management",
            Instruction: "A user who can submit a price must not also publish it",
            KnownPermissionKeys: ["price.submit", "price.publish"],
            KnownResources: ["price"],
            KnownActions: ["submit", "publish"]);

        SodRuleDraftResult result = await assistant.DraftSodRuleAsync(request, CancellationToken.None);

        // Two distinct grounded actions in first-mention order (submit before publish).
        Assert.Equal("submit", result.MatcherA.Action);
        Assert.Equal("publish", result.MatcherB.Action);
        Assert.Equal("HIGH", result.Severity);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task DraftSodRuleAsync_PrefersDistinctPermissionKeys()
    {
        var request = new SodRuleDraftRequest(
            ApplicationId: "pricing-management",
            Instruction: "Separate price.submit from price.publish",
            KnownPermissionKeys: ["price.submit", "price.publish"],
            KnownResources: ["price"],
            KnownActions: ["submit", "publish"]);

        SodRuleDraftResult result = await assistant.DraftSodRuleAsync(request, CancellationToken.None);

        Assert.Equal("price.submit", result.MatcherA.PermissionKey);
        Assert.Equal("price.publish", result.MatcherB.PermissionKey);
    }

    [Fact]
    public async Task DraftSodRuleAsync_Ungrounded_WarnsAndLeavesMatchersEmpty()
    {
        var request = new SodRuleDraftRequest(
            ApplicationId: "pricing-management",
            Instruction: "Keep the two dangerous things apart",
            KnownPermissionKeys: ["price.submit", "price.publish"],
            KnownResources: ["price"],
            KnownActions: ["submit", "publish"]);

        SodRuleDraftResult result = await assistant.DraftSodRuleAsync(request, CancellationToken.None);

        Assert.Null(result.MatcherA.PermissionKey);
        Assert.Null(result.MatcherA.Action);
        Assert.Null(result.MatcherB.PermissionKey);
        Assert.Null(result.MatcherB.Action);
        Assert.Contains(result.Warnings, w => w.Contains("ground", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExplainDecisionAsync_Denied_IncludesRemediation()
    {
        var request = new DecisionExplanationRequest(
            ApplicationId: "app-1",
            Allowed: false,
            DenyReason: "No matching role",
            SubjectType: "user",
            SubjectEmail: "user@example.com",
            ResourceType: "invoice",
            ResourceId: "123",
            Action: "read",
            MatchedRoles: [],
            MatchedPermissions: [],
            MatchedPolicies: []);

        DecisionExplanationResult result = await assistant.ExplainDecisionAsync(request, CancellationToken.None);

        Assert.Contains("DENIED", result.Narrative);
        Assert.NotEmpty(result.Remediation);
    }

    [Fact]
    public async Task ExplainDecisionAsync_Denied_UnknownSubjectWithSimilarEmail_SuggestsTypo()
    {
        var diagnostics = Diagnostics(
            permissionExists: true,
            subjectKnown: false,
            similarKnownSubjectEmails: ["user7.lead@icis.com"]);

        var request = new DecisionExplanationRequest(
            ApplicationId: "app-1",
            Allowed: false,
            DenyReason: "NO_ACTIVE_ASSIGNMENT",
            SubjectType: "user",
            SubjectEmail: "user7.led@icis.com",
            ResourceType: "price",
            ResourceId: "PX-1",
            Action: "publish",
            MatchedRoles: [],
            MatchedPermissions: [],
            MatchedPolicies: [],
            Diagnostics: diagnostics);

        DecisionExplanationResult result = await assistant.ExplainDecisionAsync(request, CancellationToken.None);

        Assert.Contains("misspelled", result.Narrative, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Remediation, step => step.Contains("user7.lead@icis.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExplainDecisionAsync_Denied_PermissionNotGranted_SuggestsGrantingRoles()
    {
        var diagnostics = Diagnostics(
            permissionExists: true,
            subjectKnown: true,
            subjectRoleKeys: ["pricing-viewer"],
            rolesGrantingPermission: ["pricing-lead", "pricing-admin"]);

        var request = new DecisionExplanationRequest(
            ApplicationId: "app-1",
            Allowed: false,
            DenyReason: "PERMISSION_NOT_GRANTED",
            SubjectType: "user",
            SubjectEmail: "viewer@icis.com",
            ResourceType: "price",
            ResourceId: "PX-1",
            Action: "publish",
            MatchedRoles: ["pricing-viewer"],
            MatchedPermissions: [],
            MatchedPolicies: [],
            Diagnostics: diagnostics);

        DecisionExplanationResult result = await assistant.ExplainDecisionAsync(request, CancellationToken.None);

        Assert.Contains("pricing-lead", result.Narrative, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Remediation, step => step.Contains("pricing-lead", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExplainDecisionAsync_Denied_UnknownResourceType_SuggestsKnownTypes()
    {
        var diagnostics = Diagnostics(
            permissionExists: false,
            resourceTypeKnown: false,
            knownResourceTypes: ["price", "index"]);

        var request = new DecisionExplanationRequest(
            ApplicationId: "app-1",
            Allowed: false,
            DenyReason: "PERMISSION_NOT_FOUND",
            SubjectType: "user",
            SubjectEmail: "user@icis.com",
            ResourceType: "prices",
            ResourceId: "PX-1",
            Action: "publish",
            MatchedRoles: [],
            MatchedPermissions: [],
            MatchedPolicies: [],
            Diagnostics: diagnostics);

        DecisionExplanationResult result = await assistant.ExplainDecisionAsync(request, CancellationToken.None);

        Assert.Contains(result.Remediation, step => step.Contains("price", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExplainDecisionAsync_Allowed_HasNoRemediation()
    {
        var request = new DecisionExplanationRequest(
            ApplicationId: "app-1",
            Allowed: true,
            DenyReason: null,
            SubjectType: "user",
            SubjectEmail: "approver@example.com",
            ResourceType: "invoice",
            ResourceId: "123",
            Action: "read",
            MatchedRoles: ["approver"],
            MatchedPermissions: ["invoice.read"],
            MatchedPolicies: []);

        DecisionExplanationResult result = await assistant.ExplainDecisionAsync(request, CancellationToken.None);

        Assert.Contains("ALLOWED", result.Narrative);
        Assert.Empty(result.Remediation);
    }

    [Fact]
    public async Task NarrateImpactAsync_WithFlips_SummarisesDirectionAndSubjects()
    {
        var request = new ImpactNarrationRequest(
            ApplicationId: "app-1",
            PolicyKey: "deny-large-correction",
            Effect: "DENY",
            EvaluatedCount: 12,
            AllowToDenyCount: 3,
            DenyToAllowCount: 0,
            SampledFromHistory: true,
            Flips:
            [
                new ImpactFlipFact("a@icis.com", "PX-1", "publish", Before: true, After: false, Reason: "EXPLICIT_DENY"),
                new ImpactFlipFact("b@icis.com", "PX-2", "publish", Before: true, After: false, Reason: "EXPLICIT_DENY"),
            ]);

        ImpactNarrationResult result = await assistant.NarrateImpactAsync(request, CancellationToken.None);

        Assert.Contains("deny-large-correction", result.Summary);
        Assert.Contains("newly be denied", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a@icis.com", result.Summary);
    }

    [Fact]
    public async Task NarrateImpactAsync_NoImpact_StatesNoOutcomesChange()
    {
        var request = new ImpactNarrationRequest(
            ApplicationId: "app-1",
            PolicyKey: "allow-publish-ready",
            Effect: "ALLOW",
            EvaluatedCount: 8,
            AllowToDenyCount: 0,
            DenyToAllowCount: 0,
            SampledFromHistory: true,
            Flips: []);

        ImpactNarrationResult result = await assistant.NarrateImpactAsync(request, CancellationToken.None);

        Assert.Contains("no outcomes", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NarrateImpactAsync_NoTraffic_ExplainsNothingToEvaluate()
    {
        var request = new ImpactNarrationRequest(
            ApplicationId: "app-1",
            PolicyKey: "deny-self-publish",
            Effect: "DENY",
            EvaluatedCount: 0,
            AllowToDenyCount: 0,
            DenyToAllowCount: 0,
            SampledFromHistory: false,
            Flips: []);

        ImpactNarrationResult result = await assistant.NarrateImpactAsync(request, CancellationToken.None);

        Assert.Contains("cannot be measured", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SummarizeFindingsAsync_NoFindings_ReturnsHealthySummary()
    {
        var request = new ConfigAdvisorRequest("app-1", []);

        ConfigAdvisorResult result = await assistant.SummarizeFindingsAsync(request, CancellationToken.None);

        Assert.Empty(result.Suggestions);
        Assert.Contains("healthy", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SummarizeFindingsAsync_WithFindings_CountsSeveritiesAndSuggestsPerFinding()
    {
        var findings = new List<ConfigFindingFact>
        {
            new("privileged-unguarded:lead", "PRIVILEGED_UNGUARDED", "HIGH", "t", "d", "ROLE", "lead"),
            new("unused-role:spare", "UNUSED_ROLE", "LOW", "t", "d", "ROLE", "spare"),
        };
        var request = new ConfigAdvisorRequest("app-1", findings);

        ConfigAdvisorResult result = await assistant.SummarizeFindingsAsync(request, CancellationToken.None);

        Assert.Contains("2", result.Summary);
        Assert.Equal(2, result.Suggestions.Count);
        Assert.Contains(result.Suggestions, suggestion => suggestion.Id == "privileged-unguarded:lead" && suggestion.SuggestedFix.Length > 0);
        Assert.Contains(result.Suggestions, suggestion => suggestion.Id == "unused-role:spare" && suggestion.SuggestedFix.Length > 0);
    }

    [Fact]
    public async Task SummarizeFindingsAsync_SynthesizesExpertStructuredReview()
    {
        // Several findings of the same kind should be synthesized into a pattern, not restated as a
        // bare count. The dominant dead-context pattern must be framed as an integration gap, the
        // structured sections must be present, and affected entities must be named.
        var findings = new List<ConfigFindingFact>
        {
            new("dead-context-attribute:price-policy:market", "DEAD_CONTEXT_ATTRIBUTE", "LOW", "t", "No recorded decision has supplied context.market.", "POLICY", "price-policy"),
            new("dead-context-attribute:status-policy:status", "DEAD_CONTEXT_ATTRIBUTE", "LOW", "t", "No recorded decision has supplied context.status.", "POLICY", "status-policy"),
            new("dead-context-attribute:delta-policy:priceDeltaPct", "DEAD_CONTEXT_ATTRIBUTE", "LOW", "t", "No recorded decision has supplied context.priceDeltaPct.", "POLICY", "delta-policy"),
        };
        var request = new ConfigAdvisorRequest("app-1", findings);

        ConfigAdvisorResult result = await assistant.SummarizeFindingsAsync(request, CancellationToken.None);

        Assert.Contains("## Executive Summary", result.Summary);
        Assert.Contains("## Key Observations", result.Summary);
        Assert.Contains("## Potential Impact", result.Summary);
        Assert.Contains("## Recommended Actions", result.Summary);
        Assert.Contains("## Overall Assessment", result.Summary);
        Assert.Contains("integration gap", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("price-policy", result.Summary);
        Assert.Equal(3, result.Suggestions.Count);
    }

    [Fact]
    public async Task SummarizeAccessReviewAsync_NoItems_ReturnsNothingToReview()
    {
        var request = new AccessReviewSummaryRequest("user-abcd1234", 0, 0, 0, 0, []);

        AccessReviewSummaryResult result = await assistant.SummarizeAccessReviewAsync(request, CancellationToken.None);

        Assert.Empty(result.Rationales);
        Assert.Contains("no active access", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SummarizeAccessReviewAsync_WithItems_CountsRecommendationsAndRationalizesEach()
    {
        var items = new List<AccessReviewItemFact>
        {
            new("id-keep", "pricing-management", "pricing-analyst", "Pricing Analyst", false, "MEDIUM", "KEEP", "Actively used (5 days ago).", 5, false, 2, ["price.view"]),
            new("id-revoke", "pricing-management", "pricing-editor", "Pricing Editor", false, "MEDIUM", "REVOKE", "Not exercised in over 60 days.", null, true, 1, ["price.review"]),
            new("id-review", "pricing-management", "pricing-admin", "Pricing Admin", true, "HIGH", "REVIEW", "Privileged role not exercised in over 60 days.", null, true, 0, ["price.publish"]),
        };
        var request = new AccessReviewSummaryRequest("user-abcd1234", 3, 1, 1, 1, items);

        AccessReviewSummaryResult result = await assistant.SummarizeAccessReviewAsync(request, CancellationToken.None);

        Assert.Contains("user-abcd1234", result.Summary);
        Assert.Contains("3", result.Summary);
        Assert.Equal(3, result.Rationales.Count);
        // Rationales are keyed by the opaque item ids the model was given (no PII).
        Assert.Contains(result.Rationales, r => r.Id == "id-review" && r.Rationale.Contains("privileged", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Rationales, r => r.Id == "id-revoke" && r.Rationale.Contains("never exercised", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NarrateAuditAsync_NoEvents_ReturnsNoActivity()
    {
        var request = new AuditNarrationRequest(
            DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, 0, [], [], [], [], []);

        AuditNarrationResult result = await assistant.NarrateAuditAsync(request, CancellationToken.None);

        Assert.Empty(result.Sections);
        Assert.Contains("No audit activity", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NarrateAuditAsync_WithEvents_SummarizesAndGroupsWithCitations()
    {
        var events = new List<AuditEventFact>
        {
            new("evt-1", "RoleCreated", "pricing-management", "person-1", null, DateTimeOffset.UtcNow.AddDays(-1), null),
            new("evt-2", "RoleCreated", "pricing-management", "person-1", null, DateTimeOffset.UtcNow.AddDays(-1), null),
            new("evt-3", "PolicyPublished", "pricing-management", "person-2", null, DateTimeOffset.UtcNow.AddDays(-2), null),
        };
        var request = new AuditNarrationRequest(
            DateTimeOffset.UtcNow.AddDays(-30),
            DateTimeOffset.UtcNow,
            3,
            [new AuditGroupFact("pricing-management", 3)],
            [new AuditGroupFact("person-1", 2), new AuditGroupFact("person-2", 1)],
            [new AuditGroupFact("RoleCreated", 2), new AuditGroupFact("PolicyPublished", 1)],
            [new AuditDenyFact("pricing-management", "no matching role", 4)],
            events);

        AuditNarrationResult result = await assistant.NarrateAuditAsync(request, CancellationToken.None);

        Assert.Contains("3", result.Summary);
        Assert.Contains("person-1", result.Summary);
        Assert.Contains("denial", result.Summary, StringComparison.OrdinalIgnoreCase);
        // One section per change type, each citing only the ids it was given (no PII, no invention).
        Assert.Equal(2, result.Sections.Count);
        AuditNarrativeSection created = Assert.Single(result.Sections, s => s.Heading == "RoleCreated");
        Assert.Equal(["evt-1", "evt-2"], created.EventIds);
    }

    private static AccessSearchPlanRequest PlanRequest(string question) => new(
        question,
        AccessSearchSchema,
        new AccessSearchVocabulary(
            PermissionKeys: ["price.view", "price.publish"],
            Resources: ["price"],
            Actions: ["view", "publish"],
            RoleKeys: ["pricing-lead", "pricing-admin"],
            ResourceIds: ["market-eu"]));

    private static readonly IReadOnlyList<AccessSearchEntitySchema> AccessSearchSchema =
    [
        new("role", ["roleKey", "name", "riskLevel", "privileged", "permission", "resource", "action"]),
        new("permission", ["permissionKey", "resource", "action", "riskLevel"]),
        new("policy", ["policyKey", "effect", "state", "permission", "resource", "action"]),
        new("assignment", ["subject", "role", "resourceType", "resourceId", "state", "source"]),
        new("subject", ["subject", "role", "permission", "resource", "action", "resourceId"]),
    ];

    [Fact]
    public async Task PlanAccessSearchAsync_WhoCanPublish_ChoosesSubjectWithActionFilter()
    {
        AccessSearchPlanResult plan = await assistant.PlanAccessSearchAsync(
            PlanRequest("Who can publish prices?"), CancellationToken.None);

        Assert.Equal("subject", plan.Entity);
        Assert.Contains(plan.Filters, f => f.Field == "action" && f.Value == "publish");
    }

    [Fact]
    public async Task PlanAccessSearchAsync_WhichRolesGrantPublish_ChoosesRole()
    {
        AccessSearchPlanResult plan = await assistant.PlanAccessSearchAsync(
            PlanRequest("Which roles can publish?"), CancellationToken.None);

        Assert.Equal("role", plan.Entity);
        Assert.Contains(plan.Filters, f => f.Field == "action" && f.Value == "publish");
    }

    [Fact]
    public async Task PlanAccessSearchAsync_GroundsOnlyKnownVocabulary()
    {
        // "delete" is not in the vocabulary, so no action filter should be emitted.
        AccessSearchPlanResult plan = await assistant.PlanAccessSearchAsync(
            PlanRequest("Who can delete prices?"), CancellationToken.None);

        Assert.Equal("subject", plan.Entity);
        Assert.DoesNotContain(plan.Filters, f => f.Value == "delete");
    }

    [Fact]
    public async Task PlanAccessSearchAsync_UsesPermissionKeyWhenMentioned()
    {
        AccessSearchPlanResult plan = await assistant.PlanAccessSearchAsync(
            PlanRequest("Who has price.publish?"), CancellationToken.None);

        Assert.Equal("subject", plan.Entity);
        Assert.Contains(plan.Filters, f => f.Field == "permission" && f.Value == "price.publish");
    }

    private static DecisionDiagnostics Diagnostics(
        bool permissionExists = true,
        bool resourceTypeKnown = true,
        IReadOnlyList<string>? availableActionsForResourceType = null,
        IReadOnlyList<string>? knownResourceTypes = null,
        bool subjectKnown = true,
        int activeAssignmentCount = 0,
        int revokedAssignmentCount = 0,
        int expiredAssignmentCount = 0,
        int pendingAssignmentCount = 0,
        IReadOnlyList<string>? subjectRoleKeys = null,
        IReadOnlyList<string>? similarKnownSubjectEmails = null,
        IReadOnlyList<string>? rolesGrantingPermission = null,
        IReadOnlyList<PolicyDiagnostic>? relevantPolicies = null,
        IReadOnlyList<string>? referencedContextAttributes = null,
        IReadOnlyList<string>? providedContextKeys = null) =>
        new(
            PermissionExists: permissionExists,
            ResourceTypeKnown: resourceTypeKnown,
            AvailableActionsForResourceType: availableActionsForResourceType ?? [],
            KnownResourceTypes: knownResourceTypes ?? [],
            SubjectKnown: subjectKnown,
            ActiveAssignmentCount: activeAssignmentCount,
            RevokedAssignmentCount: revokedAssignmentCount,
            ExpiredAssignmentCount: expiredAssignmentCount,
            PendingAssignmentCount: pendingAssignmentCount,
            SubjectRoleKeys: subjectRoleKeys ?? [],
            SimilarKnownSubjectEmails: similarKnownSubjectEmails ?? [],
            RolesGrantingPermission: rolesGrantingPermission ?? [],
            RelevantPolicies: relevantPolicies ?? [],
            ReferencedContextAttributes: referencedContextAttributes ?? [],
            ProvidedContextKeys: providedContextKeys ?? []);
}
