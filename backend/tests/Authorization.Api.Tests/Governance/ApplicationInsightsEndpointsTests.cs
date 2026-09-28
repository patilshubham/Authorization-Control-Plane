using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Authorization.Api.Tests.Governance;

/// <summary>
/// The deterministic governance insight endpoints — configuration advisor findings and
/// separation-of-duties rules/violations — must be available whenever the caller can view the
/// application, <em>even when the AI subsystem is disabled</em>. This uses the default test host,
/// which does not enable AI, so these assertions prove the baseline signal is no longer coupled to
/// the AI master switch (previously the equivalent AI-namespaced endpoints returned 404 when AI was
/// off).
/// </summary>
public sealed class ApplicationInsightsEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public ApplicationInsightsEndpointsTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Config_ReportsAiDisabled_ByDefault()
    {
        // Sanity: the default host has AI off, so the following endpoints prove availability
        // specifically while AI is disabled.
        HttpClient client = factory.CreateAdminClient();

        using HttpResponseMessage response = await client.GetAsync("/v1/config");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("ai").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task ConfigFindings_Available_WhenAiDisabled()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.GetAsync(
            $"/v1/admin/applications/{applicationId}/insights/config-findings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("findings").ValueKind);
    }

    [Fact]
    public async Task SodRules_Available_WhenAiDisabled()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.GetAsync(
            $"/v1/admin/applications/{applicationId}/insights/sod-rules");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement rules = doc.RootElement.GetProperty("rules");
        Assert.Equal(JsonValueKind.Array, rules.ValueKind);
        Assert.Equal(0, rules.GetArrayLength());
    }

    [Fact]
    public async Task SodViolations_Available_WhenAiDisabled()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.GetAsync(
            $"/v1/admin/applications/{applicationId}/insights/sod-violations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("violations").ValueKind);
    }

    [Fact]
    public async Task ConfigFindings_AllowedForReadOnlyRole()
    {
        HttpClient admin = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(admin);

        HttpClient readOnly = factory.CreateClientForApplicationRole(applicationId, "ReadOnlyViewer");
        using HttpResponseMessage response = await readOnly.GetAsync(
            $"/v1/admin/applications/{applicationId}/insights/config-findings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Insights_ForbiddenForViewerOfAnotherApplication()
    {
        HttpClient admin = factory.CreateAdminClient();
        string appA = await CreateApplicationAsync(admin);
        string appB = await CreateApplicationAsync(admin);

        // A caller scoped only to appB must not read appA's insights.
        HttpClient scoped = factory.CreateClientForApplicationRole(appB, "ReadOnlyViewer");
        using HttpResponseMessage response = await scoped.GetAsync(
            $"/v1/admin/applications/{appA}/insights/config-findings");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Insights_UnknownApplication_ReturnsNotFound()
    {
        HttpClient client = factory.CreateAdminClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/v1/admin/applications/does-not-exist/insights/config-findings");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

        string applicationId = $"insights-app-{Guid.NewGuid():N}";
        using HttpResponseMessage appResponse = await client.PostAsJsonAsync("/v1/admin/applications", new
        {
            applicationId,
            name = applicationId,
            tenantId,
        });
        appResponse.EnsureSuccessStatusCode();
        return applicationId;
    }
}
