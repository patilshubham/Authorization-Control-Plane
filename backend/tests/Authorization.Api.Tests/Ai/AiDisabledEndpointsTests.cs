using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Authorization.Api.Tests.Ai;

/// <summary>
/// With AI disabled (the default), the config endpoint reports it off and every AI
/// endpoint is effectively absent (404).
/// </summary>
public sealed class AiDisabledEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public AiDisabledEndpointsTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Config_ReportsAiDisabled_ByDefault()
    {
        HttpClient client = factory.CreateAdminClient();

        using HttpResponseMessage response = await client.GetAsync("/v1/config");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement ai = doc.RootElement.GetProperty("ai");
        Assert.False(ai.GetProperty("enabled").GetBoolean());
        Assert.False(ai.GetProperty("features").GetProperty("policyAuthoring").GetBoolean());
        Assert.False(ai.GetProperty("features").GetProperty("decisionExplainer").GetBoolean());
    }

    [Fact]
    public async Task Config_RequiresAuthentication()
    {
        using HttpResponseMessage response = await factory.CreateClient().GetAsync("/v1/config");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PolicyDraft_ReturnsNotFound_WhenDisabled()
    {
        HttpClient client = factory.CreateAdminClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/admin/applications/any-app/ai/policy-draft",
            new { instruction = "Allow everything" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ExplainDecision_ReturnsNotFound_WhenDisabled()
    {
        HttpClient client = factory.CreateAdminClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/admin/applications/any-app/ai/explain-decision",
            new { allowed = true, resourceType = "invoice", action = "read" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ImpactAnalysis_ReturnsNotFound_WhenDisabled()
    {
        HttpClient client = factory.CreateAdminClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/admin/applications/any-app/ai/impact-analysis",
            new { policyKey = "deny-large-correction" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdvisorFindings_ReturnsNotFound_WhenDisabled()
    {
        HttpClient client = factory.CreateAdminClient();
        using HttpResponseMessage response = await client.GetAsync(
            "/v1/admin/applications/any-app/ai/advisor/findings");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdvisorSummarize_ReturnsNotFound_WhenDisabled()
    {
        HttpClient client = factory.CreateAdminClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/v1/admin/applications/any-app/ai/advisor/summarize", content: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AccessSearch_ReturnsNotFound_WhenDisabled()
    {
        HttpClient client = factory.CreateAdminClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/admin/applications/any-app/ai/access-search",
            new { question = "Who can publish prices?" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PlatformAccessSearch_ReturnsNotFound_WhenDisabled()
    {
        HttpClient client = factory.CreateAdminClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/admin/ai/access-search",
            new { question = "Who can publish prices?" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AccessReview_ReturnsNotFound_WhenDisabled()
    {
        HttpClient client = factory.CreateAdminClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/admin/ai/access-review/summarize",
            new { subjectEmail = "user13.reviewer@icis.com" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AuditNarrative_ReturnsNotFound_WhenDisabled()
    {
        HttpClient client = factory.CreateAdminClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/admin/ai/audit/narrative",
            new { });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AiUsage_ReturnsNotFound_WhenDisabled()
    {
        HttpClient client = factory.CreateAdminClient();
        using HttpResponseMessage response = await client.GetAsync("/v1/admin/ai/usage");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AiPromptLogs_ReturnsNotFound_WhenDisabled()
    {
        HttpClient client = factory.CreateAdminClient();
        using HttpResponseMessage response = await client.GetAsync("/v1/admin/ai/prompt-logs");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SodRules_ReturnsNotFound_WhenDisabled()
    {
        HttpClient client = factory.CreateAdminClient();
        using HttpResponseMessage response = await client.GetAsync(
            "/v1/admin/applications/any-app/ai/sod/rules");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SodViolations_ReturnsNotFound_WhenDisabled()
    {
        HttpClient client = factory.CreateAdminClient();
        using HttpResponseMessage response = await client.GetAsync(
            "/v1/admin/applications/any-app/ai/sod/violations");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SodRuleDraft_ReturnsNotFound_WhenDisabled()
    {
        HttpClient client = factory.CreateAdminClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/admin/applications/any-app/ai/sod/rules/draft",
            new { instruction = "separate submit and publish" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SodRuleSave_ReturnsNotFound_WhenDisabled()
    {
        HttpClient client = factory.CreateAdminClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/admin/applications/any-app/ai/sod/rules",
            new
            {
                ruleKey = "rule-1",
                name = "Rule 1",
                severity = "HIGH",
                matcherA = new { action = "submit" },
                matcherB = new { action = "publish" },
            });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SodRuleDelete_ReturnsNotFound_WhenDisabled()
    {
        HttpClient client = factory.CreateAdminClient();
        using HttpResponseMessage response = await client.DeleteAsync(
            "/v1/admin/applications/any-app/ai/sod/rules/rule-1");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
