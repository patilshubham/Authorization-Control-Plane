using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Authorization.Api.Tests.Governance;

/// <summary>
/// Covers the audit-sourced policy change history endpoint.
/// </summary>
public sealed class PolicyLifecycleEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private const string UnconditionalConditions = "{\"conditions\":[]}";

    private readonly TestWebApplicationFactory factory;

    public PolicyLifecycleEndpointsTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task History_IncludesCreationEvent()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(client);
        await CreatePermissionAsync(client, applicationId, "price.publish", "price", "publish");
        string policyKey = await CreatePublishedPolicyAsync(client, applicationId, "price.publish");

        using HttpResponseMessage response = await client.GetAsync(
            $"/v1/admin/applications/{applicationId}/policies/{policyKey}/history");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement entries = doc.RootElement.GetProperty("entries");
        Assert.Equal(JsonValueKind.Array, entries.ValueKind);
        Assert.Contains(
            entries.EnumerateArray(),
            e => e.GetProperty("eventType").GetString() == "POLICY_CREATED");
    }

    private static async Task<string> CreatePublishedPolicyAsync(HttpClient client, string applicationId, string permissionKey)
    {
        string policyKey = $"pol-{Guid.NewGuid():N}";
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/policies",
            new
            {
                policyKey,
                permissionKey,
                effect = "ALLOW",
                conditions = UnconditionalConditions,
                priority = 0,
                publish = true,
            });
        response.EnsureSuccessStatusCode();
        return policyKey;
    }

    private static async Task CreatePermissionAsync(HttpClient client, string applicationId, string permissionKey, string resource, string action)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/permissions",
            new { permissionKey, resource, action, riskLevel = "LOW" });
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

        string applicationId = $"pol-app-{Guid.NewGuid():N}";
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
