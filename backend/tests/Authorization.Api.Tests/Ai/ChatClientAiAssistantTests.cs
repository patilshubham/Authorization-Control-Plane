using System.Text.Json;
using Authorization.Ai;
using Authorization.Ai.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authorization.Api.Tests.Ai;

/// <summary>
/// Unit tests for the live <see cref="ChatClientAiAssistant"/> using a stubbed chat client, so
/// no network or API key is involved. These cover prompt/response parsing and graceful fallback
/// for malformed model output.
/// </summary>
public sealed class ChatClientAiAssistantTests
{
    [Fact]
    public async Task DraftPolicyAsync_ParsesSummaryAndConditions()
    {
        var stub = new StubChatCompletionClient(
            "{\"summary\":\"Allow small EU requests\",\"effect\":\"ALLOW\",\"conditions\":{\"match\":\"all\",\"conditions\":[{\"attribute\":\"context.amount\",\"operator\":\"lt\",\"value\":\"5000\"}]}}");
        ChatClientAiAssistant assistant = Create(stub);

        PolicyDraftResult result = await assistant.DraftPolicyAsync(
            new PolicyDraftRequest("app-1", "Allow when amount under 5000", ["invoice.read"], []),
            CancellationToken.None);

        Assert.Equal("Allow small EU requests", result.Summary);
        Assert.Equal("ALLOW", result.SuggestedEffect);
        Assert.Empty(result.Warnings);
        using JsonDocument doc = JsonDocument.Parse(result.ConditionsJson);
        Assert.Equal("all", doc.RootElement.GetProperty("match").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("conditions").GetArrayLength());
    }

    [Fact]
    public async Task DraftPolicyAsync_ParsesDenyEffect()
    {
        var stub = new StubChatCompletionClient(
            "{\"summary\":\"Block Pakistan\",\"effect\":\"DENY\",\"conditions\":{\"match\":\"all\",\"conditions\":[{\"attribute\":\"context.country\",\"operator\":\"eq\",\"value\":\"Pakistan\"}]}}");
        ChatClientAiAssistant assistant = Create(stub);

        PolicyDraftResult result = await assistant.DraftPolicyAsync(
            new PolicyDraftRequest("app-1", "Deny if country is Pakistan", [], []),
            CancellationToken.None);

        Assert.Equal("DENY", result.SuggestedEffect);
        using JsonDocument doc = JsonDocument.Parse(result.ConditionsJson);
        Assert.Equal("Pakistan", doc.RootElement.GetProperty("conditions")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task DraftPolicyAsync_ModelOmitsEffect_FallsBackToInstructionIntent()
    {
        // The live model sometimes returns only conditions and folds the intent into the summary,
        // omitting the structured effect field. The instruction still expresses a deny, so the
        // safety-net inference must surface DENY rather than defaulting the author to ALLOW.
        var stub = new StubChatCompletionClient(
            "{\"summary\":\"Deny access if the country is Pakistan\",\"conditions\":{\"match\":\"all\",\"conditions\":[{\"attribute\":\"context.country\",\"operator\":\"eq\",\"value\":\"Pakistan\"}]}}");
        ChatClientAiAssistant assistant = Create(stub);

        PolicyDraftResult result = await assistant.DraftPolicyAsync(
            new PolicyDraftRequest("app-1", "Deny if the country is Pakistan", [], []),
            CancellationToken.None);

        Assert.Equal("DENY", result.SuggestedEffect);
    }

    [Fact]
    public async Task DraftPolicyAsync_ModelOmitsEffect_DefaultsToAllowForGrantingInstruction()
    {
        var stub = new StubChatCompletionClient(
            "{\"match\":\"any\",\"conditions\":[{\"attribute\":\"context.region\",\"operator\":\"eq\",\"value\":\"EU\"}]}");
        ChatClientAiAssistant assistant = Create(stub);

        PolicyDraftResult result = await assistant.DraftPolicyAsync(
            new PolicyDraftRequest("app-1", "Allow when region is EU", [], []),
            CancellationToken.None);

        Assert.Equal("ALLOW", result.SuggestedEffect);
    }

    [Fact]
    public async Task DraftPolicyAsync_AcceptsBareConditionDocument()
    {
        var stub = new StubChatCompletionClient(
            "{\"match\":\"any\",\"conditions\":[{\"attribute\":\"context.region\",\"operator\":\"eq\",\"value\":\"EU\"}]}");
        ChatClientAiAssistant assistant = Create(stub);

        PolicyDraftResult result = await assistant.DraftPolicyAsync(
            new PolicyDraftRequest("app-1", "Region is EU", [], []),
            CancellationToken.None);

        using JsonDocument doc = JsonDocument.Parse(result.ConditionsJson);
        Assert.Equal("any", doc.RootElement.GetProperty("match").GetString());
    }

    [Fact]
    public async Task DraftPolicyAsync_StripsCodeFences()
    {
        var stub = new StubChatCompletionClient(
            "```json\n{\"match\":\"all\",\"conditions\":[]}\n```");
        ChatClientAiAssistant assistant = Create(stub);

        PolicyDraftResult result = await assistant.DraftPolicyAsync(
            new PolicyDraftRequest("app-1", "Always", [], []),
            CancellationToken.None);

        using JsonDocument doc = JsonDocument.Parse(result.ConditionsJson);
        Assert.Equal("all", doc.RootElement.GetProperty("match").GetString());
    }

    [Fact]
    public async Task DraftPolicyAsync_MalformedResponse_FallsBackToEmptyGroupWithWarning()
    {
        var stub = new StubChatCompletionClient("not json at all");
        ChatClientAiAssistant assistant = Create(stub);

        PolicyDraftResult result = await assistant.DraftPolicyAsync(
            new PolicyDraftRequest("app-1", "Garbage", [], []),
            CancellationToken.None);

        Assert.NotEmpty(result.Warnings);
        using JsonDocument doc = JsonDocument.Parse(result.ConditionsJson);
        Assert.Equal("all", doc.RootElement.GetProperty("match").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("conditions").GetArrayLength());
    }

    [Fact]
    public async Task ExplainDecisionAsync_Denied_ReturnsNarrativeAndRemediationSteps()
    {
        var stub = new StubChatCompletionClient(
            "{\"narrative\":\"Denied because no role matched.\",\"remediation\":[\"Assign the invoice-reader role.\",\"Re-run the simulation.\"]}");
        ChatClientAiAssistant assistant = Create(stub);

        DecisionExplanationResult result = await assistant.ExplainDecisionAsync(
            new DecisionExplanationRequest("app-1", false, "No matching role", "user", "u@x.com", "invoice", "123", "read", [], [], []),
            CancellationToken.None);

        Assert.Equal("Denied because no role matched.", result.Narrative);
        Assert.Equal(2, result.Remediation.Count);
        Assert.Equal("Assign the invoice-reader role.", result.Remediation[0]);
    }

    [Fact]
    public async Task ExplainDecisionAsync_Denied_ToleratesSingleStringRemediation()
    {
        var stub = new StubChatCompletionClient(
            "{\"narrative\":\"Denied.\",\"remediation\":\"Assign a role.\"}");
        ChatClientAiAssistant assistant = Create(stub);

        DecisionExplanationResult result = await assistant.ExplainDecisionAsync(
            new DecisionExplanationRequest("app-1", false, "No matching role", "user", "u@x.com", "invoice", "123", "read", [], [], []),
            CancellationToken.None);

        Assert.Single(result.Remediation);
        Assert.Equal("Assign a role.", result.Remediation[0]);
    }

    [Fact]
    public async Task ExplainDecisionAsync_Allowed_SuppressesRemediation()
    {
        var stub = new StubChatCompletionClient(
            "{\"narrative\":\"Allowed via admin role.\",\"remediation\":[\"Should be ignored.\"]}");
        ChatClientAiAssistant assistant = Create(stub);

        DecisionExplanationResult result = await assistant.ExplainDecisionAsync(
            new DecisionExplanationRequest("app-1", true, null, "user", "u@x.com", "invoice", "123", "read", ["admin"], [], []),
            CancellationToken.None);

        Assert.Equal("Allowed via admin role.", result.Narrative);
        Assert.Empty(result.Remediation);
    }

    [Fact]
    public async Task NarrateImpactAsync_ParsesSummary()
    {
        var stub = new StubChatCompletionClient(
            "{\"summary\":\"Publishing deny-large-correction denies 3 of 12 requests, affecting a@icis.com.\"}");
        ChatClientAiAssistant assistant = Create(stub);

        ImpactNarrationResult result = await assistant.NarrateImpactAsync(
            new ImpactNarrationRequest(
                "app-1", "deny-large-correction", "DENY", 12, 3, 0, SampledFromHistory: true,
                Flips: [new ImpactFlipFact("a@icis.com", "PX-1", "publish", true, false, "EXPLICIT_DENY")]),
            CancellationToken.None);

        Assert.Equal("Publishing deny-large-correction denies 3 of 12 requests, affecting a@icis.com.", result.Summary);
    }

    [Fact]
    public async Task NarrateImpactAsync_NonJsonResponse_UsesRawText()
    {
        var stub = new StubChatCompletionClient("This change denies three publishers.");
        ChatClientAiAssistant assistant = Create(stub);

        ImpactNarrationResult result = await assistant.NarrateImpactAsync(
            new ImpactNarrationRequest(
                "app-1", "deny-large-correction", "DENY", 12, 3, 0, SampledFromHistory: true, Flips: []),
            CancellationToken.None);

        Assert.Equal("This change denies three publishers.", result.Summary);
    }

    [Fact]
    public async Task NarrateImpactAsync_NoImpact_UsesDeterministicSummaryWithoutCallingModel()
    {
        var stub = new ThrowingChatCompletionClient();
        ChatClientAiAssistant assistant = Create(stub);

        ImpactNarrationResult result = await assistant.NarrateImpactAsync(
            new ImpactNarrationRequest(
                "app-1", "deny-all-publish-test", "DENY", 4, 0, 0, SampledFromHistory: false, Flips: []),
            CancellationToken.None);

        Assert.False(stub.WasCalled);
        Assert.Contains("changes no outcomes across 4 evaluated requests", result.Summary);
        Assert.Contains("lower bound", result.Summary);
    }

    [Fact]
    public async Task NarrateImpactAsync_NoTraffic_UsesDeterministicSummaryWithoutCallingModel()
    {
        var stub = new ThrowingChatCompletionClient();
        ChatClientAiAssistant assistant = Create(stub);

        ImpactNarrationResult result = await assistant.NarrateImpactAsync(
            new ImpactNarrationRequest(
                "app-1", "deny-all-publish-test", "DENY", 0, 0, 0, SampledFromHistory: false, Flips: []),
            CancellationToken.None);

        Assert.False(stub.WasCalled);
        Assert.Contains("no representative traffic", result.Summary);
    }

    [Fact]
    public async Task SummarizeFindingsAsync_ParsesSummaryAndSuggestions()
    {
        var stub = new StubChatCompletionClient(
            "{\"summary\":\"Found 2 issues.\",\"suggestions\":[{\"id\":\"unused-role:spare\",\"suggestedFix\":\"Archive the role.\"}]}");
        ChatClientAiAssistant assistant = Create(stub);

        ConfigAdvisorResult result = await assistant.SummarizeFindingsAsync(
            new ConfigAdvisorRequest("app-1",
            [
                new ConfigFindingFact("unused-role:spare", "UNUSED_ROLE", "LOW", "t", "d", "ROLE", "spare"),
                new ConfigFindingFact("orphan-permission:price.x", "ORPHAN_PERMISSION", "MEDIUM", "t", "d", "PERMISSION", "price.x"),
            ]),
            CancellationToken.None);

        Assert.Equal("Found 2 issues.", result.Summary);
        ConfigFindingSuggestion suggestion = Assert.Single(result.Suggestions);
        Assert.Equal("unused-role:spare", suggestion.Id);
        Assert.Equal("Archive the role.", suggestion.SuggestedFix);
    }

    [Fact]
    public async Task SummarizeFindingsAsync_DropsSuggestionsForUnknownFindingIds()
    {
        var stub = new StubChatCompletionClient(
            "{\"summary\":\"s\",\"suggestions\":[{\"id\":\"made-up-id\",\"suggestedFix\":\"x\"}]}");
        ChatClientAiAssistant assistant = Create(stub);

        ConfigAdvisorResult result = await assistant.SummarizeFindingsAsync(
            new ConfigAdvisorRequest("app-1",
            [
                new ConfigFindingFact("unused-role:spare", "UNUSED_ROLE", "LOW", "t", "d", "ROLE", "spare"),
            ]),
            CancellationToken.None);

        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public async Task SummarizeFindingsAsync_NoFindings_SkipsModel()
    {
        var stub = new ThrowingChatCompletionClient();
        ChatClientAiAssistant assistant = Create(stub);

        ConfigAdvisorResult result = await assistant.SummarizeFindingsAsync(
            new ConfigAdvisorRequest("app-1", []),
            CancellationToken.None);

        Assert.False(stub.WasCalled);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public async Task SummarizeFindingsAsync_NonJsonResponse_UsesRawTextWithoutSuggestions()
    {
        var stub = new StubChatCompletionClient("Two governance issues need attention.");
        ChatClientAiAssistant assistant = Create(stub);

        ConfigAdvisorResult result = await assistant.SummarizeFindingsAsync(
            new ConfigAdvisorRequest("app-1",
            [
                new ConfigFindingFact("unused-role:spare", "UNUSED_ROLE", "LOW", "t", "d", "ROLE", "spare"),
            ]),
            CancellationToken.None);

        Assert.Equal("Two governance issues need attention.", result.Summary);
        Assert.Empty(result.Suggestions);
    }

    private static readonly IReadOnlyList<AccessSearchEntitySchema> AccessSearchSchema =
    [
        new("role", ["roleKey", "name", "riskLevel", "privileged", "permission", "resource", "action"]),
        new("subject", ["subject", "role", "permission", "resource", "action", "resourceId"]),
    ];

    private static AccessSearchPlanRequest PlanRequest(string question) => new(
        question,
        AccessSearchSchema,
        new AccessSearchVocabulary(["price.publish"], ["price"], ["publish"], ["pricing-lead"], []));

    [Fact]
    public async Task PlanAccessSearchAsync_ParsesEntityAndFilters()
    {
        var stub = new StubChatCompletionClient(
            "{\"entity\":\"subject\",\"filters\":[{\"field\":\"action\",\"operator\":\"eq\",\"value\":\"publish\"}],\"explanation\":\"Subjects who can publish.\"}");
        ChatClientAiAssistant assistant = Create(stub);

        AccessSearchPlanResult plan = await assistant.PlanAccessSearchAsync(
            PlanRequest("Who can publish prices?"), CancellationToken.None);

        Assert.Equal("subject", plan.Entity);
        AccessSearchPlanFilter filter = Assert.Single(plan.Filters);
        Assert.Equal("action", filter.Field);
        Assert.Equal("eq", filter.Operator);
        Assert.Equal("publish", filter.Value);
        Assert.Equal("Subjects who can publish.", plan.Explanation);
    }

    [Fact]
    public async Task PlanAccessSearchAsync_ParsesLimit()
    {
        var stub = new StubChatCompletionClient(
            "{\"entity\":\"role\",\"filters\":[],\"limit\":5,\"explanation\":\"Top 5 roles.\"}");
        ChatClientAiAssistant assistant = Create(stub);

        AccessSearchPlanResult plan = await assistant.PlanAccessSearchAsync(
            PlanRequest("show me the top 5 roles"), CancellationToken.None);

        Assert.Equal("role", plan.Entity);
        Assert.Equal(5, plan.Limit);
    }

    [Fact]
    public async Task PlanAccessSearchAsync_IgnoresNonPositiveLimit()
    {
        var stub = new StubChatCompletionClient(
            "{\"entity\":\"role\",\"filters\":[],\"limit\":0}");
        ChatClientAiAssistant assistant = Create(stub);

        AccessSearchPlanResult plan = await assistant.PlanAccessSearchAsync(
            PlanRequest("list roles"), CancellationToken.None);

        Assert.Null(plan.Limit);
    }

    [Fact]
    public async Task PlanAccessSearchAsync_ParsesAbsentRelationship()
    {
        var stub = new StubChatCompletionClient(
            "{\"entity\":\"role\",\"filters\":[],\"absentRelationship\":\"assignments\",\"explanation\":\"Roles with no assignments.\"}");
        ChatClientAiAssistant assistant = Create(stub);

        AccessSearchPlanResult plan = await assistant.PlanAccessSearchAsync(
            PlanRequest("which roles have no assignments?"), CancellationToken.None);

        Assert.Equal("role", plan.Entity);
        Assert.Equal("assignments", plan.AbsentRelationship);
    }

    [Fact]
    public async Task PlanAccessSearchAsync_ParsesIncludeChild()
    {
        var stub = new StubChatCompletionClient(
            "{\"entity\":\"role\",\"filters\":[],\"include\":\"permissions\",\"includeChild\":\"policies\",\"explanation\":\"Roles, permissions, policies.\"}");
        ChatClientAiAssistant assistant = Create(stub);

        AccessSearchPlanResult plan = await assistant.PlanAccessSearchAsync(
            PlanRequest("show roles, their permissions and each permission's policies"), CancellationToken.None);

        Assert.Equal("permissions", plan.Include);
        Assert.Equal("policies", plan.IncludeChild);
    }

    [Fact]
    public async Task PlanAccessSearchAsync_ParsesIncludeGrandchild()
    {
        var stub = new StubChatCompletionClient(
            "{\"entity\":\"tenant\",\"filters\":[],\"include\":\"roles\",\"includeChild\":\"permissions\",\"includeGrandchild\":\"policies\",\"explanation\":\"Tenants, roles, permissions, policies.\"}");
        ChatClientAiAssistant assistant = Create(stub);

        AccessSearchPlanResult plan = await assistant.PlanAccessSearchAsync(
            PlanRequest("give tenant to role to permission to policy mapping for all tenants"), CancellationToken.None);

        Assert.Equal("roles", plan.Include);
        Assert.Equal("permissions", plan.IncludeChild);
        Assert.Equal("policies", plan.IncludeGrandchild);
    }

    [Fact]
    public async Task PlanAccessSearchAsync_ParsesCompoundPresenceAbsence()
    {
        var stub = new StubChatCompletionClient(
            "{\"entity\":\"role\",\"filters\":[],\"presentRelationship\":\"permissions\",\"absentRelationship\":\"assignments\",\"explanation\":\"Unused privileged roles.\"}");
        ChatClientAiAssistant assistant = Create(stub);

        AccessSearchPlanResult plan = await assistant.PlanAccessSearchAsync(
            PlanRequest("roles that grant permissions but have no assignments"), CancellationToken.None);

        Assert.Equal("permissions", plan.PresentRelationship);
        Assert.Equal("assignments", plan.AbsentRelationship);
    }

    [Fact]
    public async Task PlanAccessSearchAsync_ParsesSecondaryGroupField()
    {
        var stub = new StubChatCompletionClient(
            "{\"entity\":\"role\",\"filters\":[],\"groupByField\":\"riskLevel\",\"groupBySecondaryField\":\"status\"}");
        ChatClientAiAssistant assistant = Create(stub);

        AccessSearchPlanResult plan = await assistant.PlanAccessSearchAsync(
            PlanRequest("break roles down by risk level then status"), CancellationToken.None);

        Assert.Equal("riskLevel", plan.GroupByField);
        Assert.Equal("status", plan.GroupBySecondaryField);
    }

    [Fact]
    public async Task PlanAccessSearchAsync_RunsAtDeterministicTemperature()
    {
        var stub = new StubChatCompletionClient("{\"entity\":\"role\",\"filters\":[]}");
        ChatClientAiAssistant assistant = Create(stub);

        await assistant.PlanAccessSearchAsync(PlanRequest("list roles"), CancellationToken.None);

        // Structured spec extraction must be deterministic → temperature 0.
        Assert.Equal(0d, stub.LastTemperature);
    }

    [Fact]
    public async Task SummarizeFindingsAsync_RunsAtConfiguredAdvisoryTemperature()
    {
        var stub = new StubChatCompletionClient("{\"summary\":\"ok\",\"suggestions\":[]}");
        ChatClientAiAssistant assistant = Create(stub);

        await assistant.SummarizeFindingsAsync(
            new ConfigAdvisorRequest("app-1", [new ConfigFindingFact("id", "UNUSED_ROLE", "LOW", "t", "d", "ROLE", "r")]),
            CancellationToken.None);

        // A narrative summary runs at the ConfigAdvisor feature's configured temperature (default 0.2).
        Assert.Equal(0.2d, stub.LastTemperature);
    }

    [Fact]
    public async Task SummarizeFindingsAsync_HonorsPerFeatureTemperatureOverride()
    {
        var stub = new StubChatCompletionClient("{\"summary\":\"ok\",\"suggestions\":[]}");
        var options = new AiOptions
        {
            Features = new AiFeatureOptions
            {
                ConfigAdvisor = new AiFeatureSetting { Enabled = true, Temperature = 0.7 },
            },
        };
        ChatClientAiAssistant assistant = Create(stub, options);

        await assistant.SummarizeFindingsAsync(
            new ConfigAdvisorRequest("app-1", [new ConfigFindingFact("id", "UNUSED_ROLE", "LOW", "t", "d", "ROLE", "r")]),
            CancellationToken.None);

        // The per-feature temperature from configuration flows through to the model call.
        Assert.Equal(0.7d, stub.LastTemperature);
    }

    [Fact]
    public async Task PlanAccessSearchAsync_StripsCodeFences()
    {
        var stub = new StubChatCompletionClient(
            "```json\n{\"entity\":\"role\",\"filters\":[],\"explanation\":\"All roles.\"}\n```");
        ChatClientAiAssistant assistant = Create(stub);

        AccessSearchPlanResult plan = await assistant.PlanAccessSearchAsync(
            PlanRequest("List roles"), CancellationToken.None);

        Assert.Equal("role", plan.Entity);
        Assert.Empty(plan.Filters);
    }

    [Fact]
    public async Task PlanAccessSearchAsync_NonJsonResponse_ReturnsEmptyEntity()
    {
        var stub = new StubChatCompletionClient("I cannot answer that.");
        ChatClientAiAssistant assistant = Create(stub);

        AccessSearchPlanResult plan = await assistant.PlanAccessSearchAsync(
            PlanRequest("nonsense"), CancellationToken.None);

        Assert.Equal(string.Empty, plan.Entity);
        Assert.Empty(plan.Filters);
    }

    [Fact]
    public async Task PlanAccessSearchAsync_DropsFiltersMissingFieldOrValue()
    {
        var stub = new StubChatCompletionClient(
            "{\"entity\":\"role\",\"filters\":[{\"operator\":\"eq\",\"value\":\"x\"},{\"field\":\"action\",\"operator\":\"eq\"}],\"explanation\":\"\"}");
        ChatClientAiAssistant assistant = Create(stub);

        AccessSearchPlanResult plan = await assistant.PlanAccessSearchAsync(
            PlanRequest("roles"), CancellationToken.None);

        Assert.Equal("role", plan.Entity);
        Assert.Empty(plan.Filters);
    }

    [Fact]
    public async Task PlanAccessSearchAsync_UnmappedQuestion_ReadsGroundedSuggestions()
    {
        var stub = new StubChatCompletionClient(
            "{\"entity\":\"\",\"filters\":[],\"explanation\":\"\",\"suggestions\":[\"Which roles are privileged?\",\"How many assignments are in state EXPIRED?\"]}");
        ChatClientAiAssistant assistant = Create(stub);

        AccessSearchPlanResult plan = await assistant.PlanAccessSearchAsync(
            PlanRequest("what is the meaning of life?"), CancellationToken.None);

        Assert.Equal(string.Empty, plan.Entity);
        Assert.NotNull(plan.Suggestions);
        Assert.Equal(2, plan.Suggestions!.Count);
        Assert.Contains("Which roles are privileged?", plan.Suggestions);
    }

    [Fact]
    public async Task PlanAccessSearchAsync_PayloadTooLarge_DownshiftsAndSucceeds()
    {
        // The provider rejects the first (full) attempt as too large; the planner must retry with a
        // smaller prompt (vocabulary dropped) rather than surfacing a hard failure.
        var stub = new DownshiftingChatCompletionClient(
            failuresBeforeSuccess: 1,
            successResponse: "{\"entity\":\"role\",\"filters\":[],\"explanation\":\"All roles.\"}");
        ChatClientAiAssistant assistant = Create(stub);

        AccessSearchPlanResult plan = await assistant.PlanAccessSearchAsync(
            PlanRequest("List roles"), CancellationToken.None);

        Assert.Equal("role", plan.Entity);
        Assert.Equal(2, stub.Prompts.Count);
        // The full attempt included the grounding vocabulary; the successful retry dropped it.
        Assert.Contains("price.publish", stub.Prompts[0]);
        Assert.DoesNotContain("price.publish", stub.Prompts[1]);
    }

    [Fact]
    public async Task PlanAccessSearchAsync_PayloadTooLarge_Persistent_StillReturnsMinimalPrompt()
    {
        // Even when every level is rejected until the minimum, the last attempt (schema names only)
        // is made and its result returned — the feature degrades, it does not crash.
        var stub = new DownshiftingChatCompletionClient(
            failuresBeforeSuccess: 2,
            successResponse: "{\"entity\":\"role\",\"filters\":[],\"explanation\":\"All roles.\"}");
        ChatClientAiAssistant assistant = Create(stub);

        AccessSearchPlanResult plan = await assistant.PlanAccessSearchAsync(
            PlanRequest("List roles"), CancellationToken.None);

        Assert.Equal("role", plan.Entity);
        Assert.Equal(3, stub.Prompts.Count);
    }

    [Fact]
    public async Task DraftSodRuleAsync_ParsesBothMatchers()
    {
        var stub = new StubChatCompletionClient(
            "{\"name\":\"Segregate submit and publish\",\"rationale\":\"No one should do both\",\"severity\":\"critical\",\"matcherA\":{\"action\":\"submit\"},\"matcherB\":{\"action\":\"publish\"},\"warnings\":[]}");
        ChatClientAiAssistant assistant = Create(stub);

        SodRuleDraftResult result = await assistant.DraftSodRuleAsync(
            new SodRuleDraftRequest("pricing-management", "submit vs publish", ["price.submit", "price.publish"], ["price"], ["submit", "publish"]),
            CancellationToken.None);

        Assert.Equal("Segregate submit and publish", result.Name);
        Assert.Equal("CRITICAL", result.Severity);
        Assert.Equal("submit", result.MatcherA.Action);
        Assert.Equal("publish", result.MatcherB.Action);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task DraftSodRuleAsync_EmptyMatcher_AddsWarning()
    {
        var stub = new StubChatCompletionClient(
            "{\"name\":\"Partial\",\"severity\":\"high\",\"matcherA\":{\"action\":\"submit\"},\"matcherB\":{},\"warnings\":[]}");
        ChatClientAiAssistant assistant = Create(stub);

        SodRuleDraftResult result = await assistant.DraftSodRuleAsync(
            new SodRuleDraftRequest("pricing-management", "submit vs ?", ["price.submit"], ["price"], ["submit"]),
            CancellationToken.None);

        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task DraftSodRuleAsync_NonJson_ReturnsEmptyDraftWithWarning()
    {
        var stub = new StubChatCompletionClient("I cannot help with that.");
        ChatClientAiAssistant assistant = Create(stub);

        SodRuleDraftResult result = await assistant.DraftSodRuleAsync(
            new SodRuleDraftRequest("pricing-management", "nonsense", [], [], []),
            CancellationToken.None);

        Assert.Null(result.MatcherA.Action);
        Assert.Null(result.MatcherB.Action);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task SummarizeAccessReviewAsync_ParsesSummaryAndRationales()
    {
        var stub = new StubChatCompletionClient(
            "{\"summary\":\"user-abcd1234 holds 2 grants; review the flagged ones.\",\"rationales\":[{\"id\":\"id-review\",\"rationale\":\"Privileged and unused.\"}]}");
        ChatClientAiAssistant assistant = Create(stub);

        AccessReviewSummaryResult result = await assistant.SummarizeAccessReviewAsync(
            ReviewRequest(), CancellationToken.None);

        Assert.Contains("review the flagged", result.Summary);
        AccessReviewItemRationale rationale = Assert.Single(result.Rationales);
        Assert.Equal("id-review", rationale.Id);
        Assert.Equal("Privileged and unused.", rationale.Rationale);
    }

    [Fact]
    public async Task SummarizeAccessReviewAsync_DropsRationalesForUnknownItemIds()
    {
        var stub = new StubChatCompletionClient(
            "{\"summary\":\"s\",\"rationales\":[{\"id\":\"made-up-id\",\"rationale\":\"x\"}]}");
        ChatClientAiAssistant assistant = Create(stub);

        AccessReviewSummaryResult result = await assistant.SummarizeAccessReviewAsync(
            ReviewRequest(), CancellationToken.None);

        Assert.Empty(result.Rationales);
    }

    [Fact]
    public async Task SummarizeAccessReviewAsync_NoItems_SkipsModel()
    {
        var stub = new ThrowingChatCompletionClient();
        ChatClientAiAssistant assistant = Create(stub);

        AccessReviewSummaryResult result = await assistant.SummarizeAccessReviewAsync(
            new AccessReviewSummaryRequest("user-abcd1234", 0, 0, 0, 0, []),
            CancellationToken.None);

        Assert.False(stub.WasCalled);
        Assert.Empty(result.Rationales);
    }

    [Fact]
    public async Task SummarizeAccessReviewAsync_NonJsonResponse_UsesRawTextWithoutRationales()
    {
        var stub = new StubChatCompletionClient("This user should be recertified with care.");
        ChatClientAiAssistant assistant = Create(stub);

        AccessReviewSummaryResult result = await assistant.SummarizeAccessReviewAsync(
            ReviewRequest(), CancellationToken.None);

        Assert.Equal("This user should be recertified with care.", result.Summary);
        Assert.Empty(result.Rationales);
    }

    [Fact]
    public async Task NarrateAuditAsync_ParsesSummaryAndSections()
    {
        var stub = new StubChatCompletionClient(
            "{\"summary\":\"person-1 made 2 changes.\",\"sections\":[{\"heading\":\"Role changes\",\"detail\":\"Two roles created.\",\"eventIds\":[\"evt-1\",\"evt-2\"]}]}");
        ChatClientAiAssistant assistant = Create(stub);

        AuditNarrationResult result = await assistant.NarrateAuditAsync(AuditRequest(), CancellationToken.None);

        Assert.Contains("person-1", result.Summary);
        AuditNarrativeSection section = Assert.Single(result.Sections);
        Assert.Equal("Role changes", section.Heading);
        Assert.Equal(["evt-1", "evt-2"], section.EventIds);
    }

    [Fact]
    public async Task NarrateAuditAsync_DropsEventIdsForUnknownEvents()
    {
        var stub = new StubChatCompletionClient(
            "{\"summary\":\"s\",\"sections\":[{\"heading\":\"h\",\"detail\":\"d\",\"eventIds\":[\"evt-1\",\"made-up\"]}]}");
        ChatClientAiAssistant assistant = Create(stub);

        AuditNarrationResult result = await assistant.NarrateAuditAsync(AuditRequest(), CancellationToken.None);

        AuditNarrativeSection section = Assert.Single(result.Sections);
        // The invented id is dropped; only the real event survives.
        Assert.Equal(["evt-1"], section.EventIds);
    }

    [Fact]
    public async Task NarrateAuditAsync_NoEvents_SkipsModel()
    {
        var stub = new ThrowingChatCompletionClient();
        ChatClientAiAssistant assistant = Create(stub);

        AuditNarrationResult result = await assistant.NarrateAuditAsync(
            new AuditNarrationRequest(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, 0, [], [], [], [], []),
            CancellationToken.None);

        Assert.False(stub.WasCalled);
        Assert.Empty(result.Sections);
    }

    [Fact]
    public async Task NarrateAuditAsync_NonJsonResponse_UsesRawTextWithoutSections()
    {
        var stub = new StubChatCompletionClient("A quiet week with routine changes.");
        ChatClientAiAssistant assistant = Create(stub);

        AuditNarrationResult result = await assistant.NarrateAuditAsync(AuditRequest(), CancellationToken.None);

        Assert.Equal("A quiet week with routine changes.", result.Summary);
        Assert.Empty(result.Sections);
    }

    private static AuditNarrationRequest AuditRequest() => new(
        DateTimeOffset.UtcNow.AddDays(-30),
        DateTimeOffset.UtcNow,
        2,
        [new AuditGroupFact("pricing-management", 2)],
        [new AuditGroupFact("person-1", 2)],
        [new AuditGroupFact("RoleCreated", 2)],
        [],
        [
            new AuditEventFact("evt-1", "RoleCreated", "pricing-management", "person-1", null, DateTimeOffset.UtcNow.AddDays(-1), null),
            new AuditEventFact("evt-2", "RoleCreated", "pricing-management", "person-1", null, DateTimeOffset.UtcNow.AddDays(-1), null),
        ]);

    private static AccessReviewSummaryRequest ReviewRequest() => new(
        "user-abcd1234",
        2,
        0,
        1,
        1,
        [
            new AccessReviewItemFact("id-revoke", "pricing-management", "pricing-editor", "Pricing Editor", false, "MEDIUM", "REVOKE", "Not exercised in over 60 days.", null, true, 1, ["price.review"]),
            new AccessReviewItemFact("id-review", "pricing-management", "pricing-admin", "Pricing Admin", true, "HIGH", "REVIEW", "Privileged role not exercised in over 60 days.", null, true, 0, ["price.publish"]),
        ]);

    private static ChatClientAiAssistant Create(IChatCompletionClient client, AiOptions? options = null) =>
        new(client, options ?? new AiOptions(), NullLogger<ChatClientAiAssistant>.Instance);

    private sealed class StubChatCompletionClient(string response) : IChatCompletionClient
    {
        public double? LastTemperature { get; private set; }

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken, double? temperature = null)
        {
            LastTemperature = temperature;
            return Task.FromResult(response);
        }
    }

    // Throws AiPayloadTooLargeException for the first N calls (simulating HTTP 413), then returns a
    // valid response. Records every user prompt so tests can assert the payload actually shrank.
    private sealed class DownshiftingChatCompletionClient(int failuresBeforeSuccess, string successResponse) : IChatCompletionClient
    {
        private int calls;

        public List<string> Prompts { get; } = [];

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken, double? temperature = null)
        {
            Prompts.Add(userPrompt);
            if (calls++ < failuresBeforeSuccess)
            {
                throw new AiPayloadTooLargeException("payload too large");
            }

            return Task.FromResult(successResponse);
        }
    }

    private sealed class ThrowingChatCompletionClient : IChatCompletionClient
    {
        public bool WasCalled { get; private set; }

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken, double? temperature = null)
        {
            WasCalled = true;
            throw new InvalidOperationException("The model must not be called for the no-impact or no-traffic cases.");
        }
    }
}
