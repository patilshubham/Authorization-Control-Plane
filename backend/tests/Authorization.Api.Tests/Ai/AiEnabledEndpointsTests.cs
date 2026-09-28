using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Authorization.Api.Tests.Ai;

/// <summary>
/// With AI enabled against the deterministic stub provider, the config endpoint
/// reports it on and the F1/F2 endpoints return advisory output. Capability gating
/// is still enforced.
/// </summary>
public sealed class AiEnabledEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;
    private readonly WebApplicationFactory<Program> aiHost;

    public AiEnabledEndpointsTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
        aiHost = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Ai:Enabled", "true");
            builder.UseSetting("Ai:Provider", "Fake");
        });
    }

    [Fact]
    public async Task Config_ReportsAiEnabled()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);

        using HttpResponseMessage response = await client.GetAsync("/v1/config");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement ai = doc.RootElement.GetProperty("ai");
        Assert.True(ai.GetProperty("enabled").GetBoolean());
        Assert.True(ai.GetProperty("features").GetProperty("policyAuthoring").GetBoolean());

        // The configured generation parameters are surfaced (non-sensitive) so the portal can show
        // the active temperature/limits; the API key is never part of this response.
        JsonElement limits = ai.GetProperty("limits");
        Assert.True(limits.TryGetProperty("temperature", out JsonElement temperature));
        Assert.True(temperature.GetDouble() >= 0d);
        Assert.True(limits.GetProperty("maxTokens").GetInt32() > 0);
    }

    [Fact]
    public async Task Config_ReportsPortalRuntimeDefaults()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);

        using HttpResponseMessage response = await client.GetAsync("/v1/config");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;

        JsonElement pagination = root.GetProperty("pagination");
        Assert.Equal(25, pagination.GetProperty("defaultPageSize").GetInt32());
        Assert.Equal(200, pagination.GetProperty("maxPageSize").GetInt32());
        Assert.Equal(
            new[] { 10, 25, 50, 100 },
            pagination.GetProperty("pageSizeOptions").EnumerateArray().Select(e => e.GetInt32()).ToArray());

        JsonElement cache = root.GetProperty("cache");
        Assert.Equal(30_000, cache.GetProperty("defaultStaleMs").GetInt32());
        Assert.Equal(10_000, cache.GetProperty("volatileStaleMs").GetInt32());
        Assert.Equal(300_000, cache.GetProperty("configStaleMs").GetInt32());

        JsonElement ui = root.GetProperty("ui");
        Assert.Equal(
            new[] { 7, 30, 90 },
            ui.GetProperty("aiReportingWindows").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Equal(14, ui.GetProperty("activityTrendDays").GetInt32());
        Assert.Equal(200, ui.GetProperty("auditPageSize").GetInt32());

        JsonElement roleLabels = root.GetProperty("roleLabels");
        Assert.Equal("Platform Super Admin", roleLabels.GetProperty("platformsuperadmin").GetString());
        Assert.Equal("Application Admin", roleLabels.GetProperty("applicationadmin").GetString());
    }

    [Fact]
    public async Task PolicyDraft_ReturnsValidConditionsJson()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/policy-draft",
            new { instruction = "Allow when the request amount is under 5000" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string conditionsJson = doc.RootElement.GetProperty("conditionsJson").GetString()!;
        using JsonDocument conditions = JsonDocument.Parse(conditionsJson);
        Assert.True(conditions.RootElement.TryGetProperty("match", out _));
        Assert.Equal("ALLOW", doc.RootElement.GetProperty("suggestedEffect").GetString());
    }

    [Fact]
    public async Task PolicyDraft_EmptyInstruction_ReturnsValidationError()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/policy-draft",
            new { instruction = "   " });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task PolicyDraft_Forbidden_ForReadOnlyRole()
    {
        // Admin creates the app, a read-only caller then attempts to draft a policy.
        HttpClient admin = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(admin);

        HttpClient readOnly = factory.CreateClientForApplicationRole(aiHost, applicationId, "ReadOnlyViewer");
        using HttpResponseMessage response = await readOnly.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/policy-draft",
            new { instruction = "Allow everything" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ExplainDecision_ReturnsNarrative()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/explain-decision",
            new
            {
                allowed = false,
                denyReason = "No matching role",
                subjectType = "user",
                resourceType = "invoice",
                resourceId = "123",
                action = "read",
                matchedRoles = Array.Empty<string>(),
                matchedPermissions = Array.Empty<string>(),
                matchedPolicies = Array.Empty<string>(),
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("narrative").GetString()));
    }

    [Fact]
    public async Task ImpactAnalysis_UnknownDraftPolicy_ReturnsNotFound()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/impact-analysis",
            new { policyKey = "no-such-policy" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ImpactAnalysis_EmptyPolicyKey_ReturnsValidationError()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/impact-analysis",
            new { policyKey = "   " });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ImpactAnalysis_Forbidden_ForReadOnlyRole()
    {
        HttpClient admin = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(admin);

        HttpClient readOnly = factory.CreateClientForApplicationRole(aiHost, applicationId, "ReadOnlyViewer");
        using HttpResponseMessage response = await readOnly.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/impact-analysis",
            new { policyKey = "deny-large-correction" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdvisorFindings_ReturnsFindingsArray()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.GetAsync(
            $"/v1/admin/applications/{applicationId}/ai/advisor/findings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("findings").ValueKind);
    }

    [Fact]
    public async Task AdvisorFindings_AllowedForReadOnlyRole()
    {
        HttpClient admin = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(admin);

        HttpClient readOnly = factory.CreateClientForApplicationRole(aiHost, applicationId, "ReadOnlyViewer");
        using HttpResponseMessage response = await readOnly.GetAsync(
            $"/v1/admin/applications/{applicationId}/ai/advisor/findings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AdvisorSummarize_ReturnsSummaryAndFindings()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.PostAsync(
            $"/v1/admin/applications/{applicationId}/ai/advisor/summarize", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("summary").GetString()));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("findings").ValueKind);
    }

    [Fact]
    public async Task AccessSearch_ReturnsEntityAndResultsArray()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/access-search",
            new { question = "Which roles exist?" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("entity").GetString()));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("results").ValueKind);
    }

    [Fact]
    public async Task AccessSearch_EmptyQuestion_ReturnsValidationError()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/access-search",
            new { question = "   " });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task AccessSearch_AllowedForReadOnlyRole()
    {
        HttpClient admin = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(admin);

        HttpClient readOnly = factory.CreateClientForApplicationRole(aiHost, applicationId, "ReadOnlyViewer");
        using HttpResponseMessage response = await readOnly.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/access-search",
            new { question = "Which roles exist?" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PlatformAccessSearch_ReturnsResultsArray()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/admin/ai/access-search",
            new { question = "Who can publish prices?" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("results").ValueKind);
    }

    [Fact]
    public async Task PlatformAccessSearch_Count_CollapsesToSingleRow_WithSample()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        // Two applications each with a role. A platform-wide count must sum across both and return a
        // single count row (not one per app), with a bounded, citable sample of the matching records.
        string appA = await CreateApplicationAsync(client);
        string appB = await CreateApplicationAsync(client);
        await CreateRoleAsync(client, appA, "count-role-a");
        await CreateRoleAsync(client, appB, "count-role-b");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/admin/ai/access-search",
            new { question = "How many roles are there?" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;
        Assert.Equal("count", root.GetProperty("mode").GetString());

        JsonElement results = root.GetProperty("results");
        // Collapsed to a single aggregate row rather than one count per application.
        Assert.Equal(1, results.GetArrayLength());
        JsonElement countRow = results[0];
        Assert.True(int.Parse(countRow.GetProperty("title").GetString()!) >= 2);
        // The number is auditable: the matching records travel with it as a sample.
        JsonElement children = countRow.GetProperty("children");
        Assert.Equal(JsonValueKind.Array, children.ValueKind);
        Assert.Contains(children.EnumerateArray(), c => c.GetProperty("deepLinkKey").GetString() == "count-role-a");
        Assert.Contains(children.EnumerateArray(), c => c.GetProperty("deepLinkKey").GetString() == "count-role-b");
    }

    [Fact]
    public async Task AccessReview_EmptySubject_ReturnsValidationError()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/admin/ai/access-review/summarize",
            new { subjectEmail = "   " });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task AccessReview_UnknownSubject_ReturnsEmptyReview()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/admin/ai/access-review/summarize",
            new { subjectEmail = $"nobody-{Guid.NewGuid():N}@local.test" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task AccessReview_ReturnsGrantsWithRecommendationsAndAiSummary()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);
        await CreateRoleAsync(client, applicationId, "reviewer-role", privileged: true);

        string email = $"recert-{Guid.NewGuid():N}@local.test";
        using HttpResponseMessage assignResponse = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/assignments",
            new { subjectType = "USER", subjectEmail = email, roleKey = "reviewer-role", validUntil = DateTimeOffset.UtcNow.AddDays(30) });
        Assert.Equal(HttpStatusCode.Created, assignResponse.StatusCode);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/admin/ai/access-review/summarize",
            new { subjectEmail = email, applicationId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;

        // Real subject email is returned for the (authorized) browser to display.
        Assert.Equal(email, root.GetProperty("subjectEmail").GetString());
        // The deterministic AI summary is populated by the Fake provider.
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("summary").GetString()));

        JsonElement item = Assert.Single(root.GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal("reviewer-role", item.GetProperty("roleKey").GetString());
        Assert.Contains(
            item.GetProperty("recommendation").GetString(),
            new[] { "KEEP", "REVOKE", "REVIEW" });
        // Per-item rationale from the Fake provider.
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("rationale").GetString()));
    }

    [Fact]
    public async Task AccessReview_ForbiddenScoping_ExcludesUnauthorizedApplications()
    {
        // A read-only caller may run the review, but only over applications they can view.
        HttpClient admin = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(admin);
        await CreateRoleAsync(admin, applicationId, "reviewer-role", privileged: false);

        string email = $"recert-{Guid.NewGuid():N}@local.test";
        using HttpResponseMessage assignResponse = await admin.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/assignments",
            new { subjectType = "USER", subjectEmail = email, roleKey = "reviewer-role" });
        Assert.Equal(HttpStatusCode.Created, assignResponse.StatusCode);

        HttpClient readOnly = factory.CreateClientForApplicationRole(aiHost, applicationId, "ReadOnlyViewer");
        using HttpResponseMessage response = await readOnly.PostAsJsonAsync(
            "/v1/admin/ai/access-review/summarize",
            new { subjectEmail = email });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task AuditNarrative_ReturnsEventsWithAiSummary()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);
        // Creating a role records an audit event scoped to this application.
        await CreateRoleAsync(client, applicationId, "narrative-role", privileged: false);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/admin/ai/audit/narrative",
            new { applicationId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;

        Assert.True(root.GetProperty("totalEvents").GetInt32() > 0);
        // The deterministic AI summary and grounded sections are populated by the Fake provider.
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("summary").GetString()));
        Assert.True(root.GetProperty("sections").GetArrayLength() > 0);

        // The raw events carry real identities for the authorized auditor and cite concrete ids.
        JsonElement[] events = root.GetProperty("events").EnumerateArray().ToArray();
        Assert.All(events, e => Assert.Equal(applicationId, e.GetProperty("applicationId").GetString()));
        Assert.All(events, e => Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("eventId").GetString())));
    }

    [Fact]
    public async Task AuditNarrative_InvertedWindow_ReturnsValidationError()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/admin/ai/audit/narrative",
            new { fromUtc = DateTimeOffset.UtcNow, toUtc = DateTimeOffset.UtcNow.AddDays(-7) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task AuditNarrative_ForbiddenApplication_ReturnsForbidden()
    {
        HttpClient admin = factory.CreateAdminClient(aiHost);
        string appA = await CreateApplicationAsync(admin);
        string appB = await CreateApplicationAsync(admin);

        // A caller who may only audit appA cannot request appB's narrative explicitly.
        HttpClient readOnly = factory.CreateClientForApplicationRole(aiHost, appA, "ReadOnlyViewer");
        using HttpResponseMessage response = await readOnly.PostAsJsonAsync(
            "/v1/admin/ai/audit/narrative",
            new { applicationId = appB });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AuditNarrative_ScopedToAuditableApplications()
    {
        HttpClient admin = factory.CreateAdminClient(aiHost);
        string appA = await CreateApplicationAsync(admin);
        string appB = await CreateApplicationAsync(admin);
        await CreateRoleAsync(admin, appA, "role-a", privileged: false);
        await CreateRoleAsync(admin, appB, "role-b", privileged: false);

        // A caller scoped to appA must never see appB's events, even without an explicit filter.
        HttpClient readOnly = factory.CreateClientForApplicationRole(aiHost, appA, "ReadOnlyViewer");
        using HttpResponseMessage response = await readOnly.PostAsJsonAsync(
            "/v1/admin/ai/audit/narrative",
            new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement[] events = doc.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert.DoesNotContain(events, e => e.GetProperty("applicationId").GetString() == appB);
    }

    [Fact]
    public async Task AiUsage_RecordsInvocation_AndReportsIt()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);

        // A successful advisory call is recorded in the metadata-only invocation log.
        using HttpResponseMessage draft = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/policy-draft",
            new { instruction = "Allow when the request amount is under 5000" });
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);

        using HttpResponseMessage response = await client.GetAsync("/v1/admin/ai/usage?windowDays=7");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement report = doc.RootElement.GetProperty("report");
        Assert.Equal(7, doc.RootElement.GetProperty("windowDays").GetInt32());
        Assert.True(report.GetProperty("totalInvocations").GetInt32() >= 1);

        JsonElement[] byFeature = report.GetProperty("byFeature").EnumerateArray().ToArray();
        Assert.Contains(byFeature, f => f.GetProperty("feature").GetString() == "policyAuthoring");
    }

    [Fact]
    public async Task PromptLog_RecordsSuccessfulPrompt_WithInterpretation()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        await CreateApplicationAsync(client);

        string question = $"Which roles exist? marker-{Guid.NewGuid():N}";
        using HttpResponseMessage search = await client.PostAsJsonAsync(
            "/v1/admin/ai/access-search",
            new { question });
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);

        using HttpResponseMessage response = await client.GetAsync("/v1/admin/ai/prompt-logs?windowDays=7");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement item = Assert.Single(
            doc.RootElement.GetProperty("items").EnumerateArray()
                .Where(i => i.GetProperty("promptText").GetString() == question)
                .ToArray());

        Assert.Equal("accessSearch", item.GetProperty("feature").GetString());
        Assert.Equal("Succeeded", item.GetProperty("outcome").GetString());
        // The model's interpretation (the planned spec) is captured as JSON.
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("interpretation").GetString()));
    }

    [Fact]
    public async Task PromptLog_RecordsInvalidInputFailure()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);

        // An over-length instruction is rejected before the model runs; the failed prompt is still
        // captured so admins can review it.
        string marker = $"marker-{Guid.NewGuid():N}-";
        string instruction = marker + new string('a', 4100);
        using HttpResponseMessage draft = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/policy-draft",
            new { instruction });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, draft.StatusCode);

        using HttpResponseMessage response = await client.GetAsync(
            "/v1/admin/ai/prompt-logs?windowDays=7&failuresOnly=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement item = Assert.Single(
            doc.RootElement.GetProperty("items").EnumerateArray()
                .Where(i => i.GetProperty("promptText").GetString()!.StartsWith(marker, StringComparison.Ordinal))
                .ToArray());

        Assert.Equal("policyAuthoring", item.GetProperty("feature").GetString());
        Assert.Equal("InvalidInput", item.GetProperty("outcome").GetString());
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("errorMessage").GetString()));
    }

    [Fact]
    public async Task PromptLog_FailuresOnly_ExcludesSuccesses()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        await CreateApplicationAsync(client);

        string question = $"Which roles exist? ok-{Guid.NewGuid():N}";
        using HttpResponseMessage search = await client.PostAsJsonAsync(
            "/v1/admin/ai/access-search",
            new { question });
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);

        using HttpResponseMessage response = await client.GetAsync(
            "/v1/admin/ai/prompt-logs?windowDays=7&failuresOnly=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(
            doc.RootElement.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("promptText").GetString() == question);
    }

    [Fact]
    public async Task PromptLog_NotRecorded_WhenCaptureDisabled()
    {
        WebApplicationFactory<Program> noCaptureHost = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Ai:Enabled", "true");
            builder.UseSetting("Ai:Provider", "Fake");
            builder.UseSetting("Ai:Logging:CapturePrompts", "false");
        });

        HttpClient client = factory.CreateAdminClient(noCaptureHost);
        await CreateApplicationAsync(client);

        string question = $"Which roles exist? nolog-{Guid.NewGuid():N}";
        using HttpResponseMessage search = await client.PostAsJsonAsync(
            "/v1/admin/ai/access-search",
            new { question });
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);

        using HttpResponseMessage response = await client.GetAsync("/v1/admin/ai/prompt-logs?windowDays=7");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(
            doc.RootElement.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("promptText").GetString() == question);
    }

    [Fact]
    public async Task PromptLog_PaginatesResults()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        await CreateApplicationAsync(client);

        for (int i = 0; i < 2; i++)
        {
            using HttpResponseMessage search = await client.PostAsJsonAsync(
                "/v1/admin/ai/access-search",
                new { question = $"Which roles exist? page-{Guid.NewGuid():N}" });
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        }

        using HttpResponseMessage response = await client.GetAsync(
            "/v1/admin/ai/prompt-logs?windowDays=7&page=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(1, root.GetProperty("pageSize").GetInt32());
        Assert.True(root.GetProperty("total").GetInt32() >= 2);
        Assert.Equal(1, root.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task SodRules_ReturnsEmptyArrayForFreshApp()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.GetAsync(
            $"/v1/admin/applications/{applicationId}/ai/sod/rules");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("rules").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("rules").GetArrayLength());
    }

    [Fact]
    public async Task SodViolations_ReturnsEmptyArrayWhenNoRules()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.GetAsync(
            $"/v1/admin/applications/{applicationId}/ai/sod/violations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("violations").ValueKind);
    }

    [Fact]
    public async Task SodRuleDraft_EmptyInstruction_ReturnsValidationError()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/sod/rules/draft",
            new { instruction = "   " });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task SodRuleDraft_ReturnsGroundedMatchers()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);
        await CreatePermissionAsync(client, applicationId, "price.submit", "price", "submit");
        await CreatePermissionAsync(client, applicationId, "price.publish", "price", "publish");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/sod/rules/draft",
            new { instruction = "Separate price.submit from price.publish" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement matcherA = doc.RootElement.GetProperty("matcherA");
        JsonElement matcherB = doc.RootElement.GetProperty("matcherB");
        Assert.Equal("price.submit", matcherA.GetProperty("permissionKey").GetString());
        Assert.Equal("price.publish", matcherB.GetProperty("permissionKey").GetString());
    }

    [Fact]
    public async Task SodRuleSave_UnknownPermissionKey_ReturnsValidationError()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/sod/rules",
            new
            {
                ruleKey = "rule-1",
                name = "Rule 1",
                severity = "HIGH",
                matcherA = new { permissionKey = "does.not.exist" },
                matcherB = new { permissionKey = "also.missing" },
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task SodRuleSave_Valid_PersistsAndIsListable()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);
        await CreatePermissionAsync(client, applicationId, "price.submit", "price", "submit");
        await CreatePermissionAsync(client, applicationId, "price.publish", "price", "publish");

        using HttpResponseMessage saveResponse = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/sod/rules",
            new
            {
                ruleKey = "segregate-submit-publish",
                name = "Segregate submit and publish",
                rationale = "No one should do both.",
                severity = "HIGH",
                matcherA = new { action = "submit" },
                matcherB = new { action = "publish" },
            });

        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);

        using HttpResponseMessage listResponse = await client.GetAsync(
            $"/v1/admin/applications/{applicationId}/ai/sod/rules");
        using JsonDocument doc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        JsonElement rules = doc.RootElement.GetProperty("rules");
        Assert.Equal(1, rules.GetArrayLength());
        Assert.Equal("segregate-submit-publish", rules[0].GetProperty("ruleKey").GetString());
    }

    [Fact]
    public async Task SodRuleSave_IdenticalMatchers_ReturnsValidationError()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);
        await CreatePermissionAsync(client, applicationId, "price.submit", "price", "submit");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/sod/rules",
            new
            {
                ruleKey = "rule-dup",
                name = "Dup",
                severity = "HIGH",
                matcherA = new { action = "submit" },
                matcherB = new { action = "submit" },
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task SodRules_AllowedForReadOnlyRole()
    {
        HttpClient admin = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(admin);

        HttpClient readOnly = factory.CreateClientForApplicationRole(aiHost, applicationId, "ReadOnlyViewer");
        using HttpResponseMessage response = await readOnly.GetAsync(
            $"/v1/admin/applications/{applicationId}/ai/sod/rules");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SodRuleSave_ForbiddenForReadOnlyRole()
    {
        HttpClient admin = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(admin);
        await CreatePermissionAsync(admin, applicationId, "price.submit", "price", "submit");
        await CreatePermissionAsync(admin, applicationId, "price.publish", "price", "publish");

        HttpClient readOnly = factory.CreateClientForApplicationRole(aiHost, applicationId, "ReadOnlyViewer");
        using HttpResponseMessage response = await readOnly.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/sod/rules",
            new
            {
                ruleKey = "rule-1",
                name = "Rule 1",
                severity = "HIGH",
                matcherA = new { action = "submit" },
                matcherB = new { action = "publish" },
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SodRuleDraft_ForbiddenForReadOnlyRole()
    {
        HttpClient admin = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(admin);

        HttpClient readOnly = factory.CreateClientForApplicationRole(aiHost, applicationId, "ReadOnlyViewer");
        using HttpResponseMessage response = await readOnly.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/sod/rules/draft",
            new { instruction = "separate submit and publish" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SodRuleDelete_RetiresRule_AndDropsFromList()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);
        await CreatePermissionAsync(client, applicationId, "price.submit", "price", "submit");
        await CreatePermissionAsync(client, applicationId, "price.publish", "price", "publish");

        using HttpResponseMessage saveResponse = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/sod/rules",
            new
            {
                ruleKey = "segregate-submit-publish",
                name = "Segregate submit and publish",
                severity = "HIGH",
                matcherA = new { action = "submit" },
                matcherB = new { action = "publish" },
            });
        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);

        using HttpResponseMessage deleteResponse = await client.DeleteAsync(
            $"/v1/admin/applications/{applicationId}/ai/sod/rules/segregate-submit-publish");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using HttpResponseMessage listResponse = await client.GetAsync(
            $"/v1/admin/applications/{applicationId}/ai/sod/rules");
        using JsonDocument doc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("rules").GetArrayLength());
    }

    [Fact]
    public async Task SodRuleDelete_UnknownRule_ReturnsNotFound()
    {
        HttpClient client = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.DeleteAsync(
            $"/v1/admin/applications/{applicationId}/ai/sod/rules/never-created");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SodRuleDelete_ForbiddenForReadOnlyRole()
    {
        HttpClient admin = factory.CreateAdminClient(aiHost);
        string applicationId = await CreateApplicationAsync(admin);
        await CreatePermissionAsync(admin, applicationId, "price.submit", "price", "submit");
        await CreatePermissionAsync(admin, applicationId, "price.publish", "price", "publish");

        using HttpResponseMessage saveResponse = await admin.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/ai/sod/rules",
            new
            {
                ruleKey = "rule-1",
                name = "Rule 1",
                severity = "HIGH",
                matcherA = new { action = "submit" },
                matcherB = new { action = "publish" },
            });
        saveResponse.EnsureSuccessStatusCode();

        HttpClient readOnly = factory.CreateClientForApplicationRole(aiHost, applicationId, "ReadOnlyViewer");
        using HttpResponseMessage response = await readOnly.DeleteAsync(
            $"/v1/admin/applications/{applicationId}/ai/sod/rules/rule-1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task CreatePermissionAsync(HttpClient client, string applicationId, string permissionKey, string resource, string action)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/permissions",
            new { permissionKey, resource, action, riskLevel = "LOW" });
        response.EnsureSuccessStatusCode();
    }

    private static async Task CreateRoleAsync(HttpClient client, string applicationId, string roleKey, bool privileged)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/roles",
            new { roleKey, name = roleKey, privileged, riskLevel = privileged ? "HIGH" : "LOW" });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> CreateApplicationAsync(HttpClient client)
    {
        string tenantId = $"ten-{Guid.NewGuid():N}";
        using HttpResponseMessage tenantResponse = await client.PostAsJsonAsync("/v1/admin/tenants", new
        {
            tenantId,
            name = tenantId,
        });
        tenantResponse.EnsureSuccessStatusCode();

        string applicationId = $"ai-app-{Guid.NewGuid():N}";
        using HttpResponseMessage appResponse = await client.PostAsJsonAsync("/v1/admin/applications", new
        {
            applicationId,
            name = applicationId,
            tenantId,
        });
        appResponse.EnsureSuccessStatusCode();
        return applicationId;
    }

    private static async Task CreateRoleAsync(HttpClient client, string applicationId, string roleKey)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/roles",
            new { roleKey, name = roleKey, privileged = false, riskLevel = "LOW" });
        response.EnsureSuccessStatusCode();
    }
}
