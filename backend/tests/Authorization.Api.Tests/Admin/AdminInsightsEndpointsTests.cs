using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Authorization.Api.Tests.Admin;

public sealed class AdminInsightsEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public AdminInsightsEndpointsTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task PlatformOverview_ReturnsAggregateCounts()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = $"ov-app-{Guid.NewGuid():N}";
        await CreateApplicationAsync(client, applicationId);
        await CreateRoleAsync(client, applicationId, "viewer");

        using HttpResponseMessage response = await client.GetAsync("/v1/admin/overview");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("applicationCount").GetInt32() >= 1);
        Assert.True(root.GetProperty("roleCount").GetInt32() >= 1);
        Assert.True(root.TryGetProperty("applicationsByTenant", out JsonElement byTenant) && byTenant.ValueKind == JsonValueKind.Array);
        Assert.True(root.TryGetProperty("recentAudit", out JsonElement recent) && recent.ValueKind == JsonValueKind.Array);
    }

    [Fact]
    public async Task ApplicationOverview_ReturnsCountsForApplication()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = $"appov-{Guid.NewGuid():N}";
        await CreateApplicationAsync(client, applicationId);
        await CreateRoleAsync(client, applicationId, "approver");
        await CreatePermissionAsync(client, applicationId, "invoice.read");

        using HttpResponseMessage response = await client.GetAsync($"/v1/admin/applications/{applicationId}/overview");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;
        Assert.Equal(applicationId, root.GetProperty("applicationId").GetString());
        Assert.Equal(1, root.GetProperty("roleCount").GetInt32());
        Assert.Equal(1, root.GetProperty("permissionCount").GetInt32());
        Assert.True(root.TryGetProperty("roleRiskDistribution", out JsonElement risk) && risk.ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task ApplicationOverview_UnknownApplication_Returns404()
    {
        HttpClient client = factory.CreateAdminClient();
        using HttpResponseMessage response = await client.GetAsync($"/v1/admin/applications/missing-{Guid.NewGuid():N}/overview");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TenantDetail_ReturnsOwnedApplicationsAndRollup()
    {
        HttpClient client = factory.CreateAdminClient();
        string tenantId = $"ten-{Guid.NewGuid():N}";
        using HttpResponseMessage tenantResponse = await client.PostAsJsonAsync("/v1/admin/tenants", new
        {
            tenantId,
            name = "Detail Tenant",
        });
        Assert.Equal(HttpStatusCode.Created, tenantResponse.StatusCode);

        string applicationId = $"ten-app-{Guid.NewGuid():N}";
        using HttpResponseMessage appResponse = await client.PostAsJsonAsync("/v1/admin/applications", new
        {
            applicationId,
            name = "Tenant App",
            tenantAware = false,
            tenantId,
        });
        Assert.Equal(HttpStatusCode.Created, appResponse.StatusCode);

        using HttpResponseMessage response = await client.GetAsync($"/v1/admin/tenants/{tenantId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;
        Assert.Equal(tenantId, root.GetProperty("tenant").GetProperty("tenantId").GetString());
        JsonElement apps = root.GetProperty("applications");
        Assert.Contains(apps.EnumerateArray(), a => a.GetProperty("applicationId").GetString() == applicationId);
        Assert.Equal(1, root.GetProperty("rollup").GetProperty("applicationCount").GetInt32());
    }

    [Fact]
    public async Task TenantDetail_UnknownTenant_Returns404()
    {
        HttpClient client = factory.CreateAdminClient();
        using HttpResponseMessage response = await client.GetAsync($"/v1/admin/tenants/missing-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UsersDirectory_AggregatesAssignmentsBySubject()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = $"user-app-{Guid.NewGuid():N}";
        await CreateApplicationAsync(client, applicationId);
        await CreateRoleAsync(client, applicationId, "member");

        string email = $"user-{Guid.NewGuid():N}@local.test";
        using HttpResponseMessage assignResponse = await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/assignments", new
        {
            subjectType = "USER",
            subjectEmail = email,
            roleKey = "member",
        });
        Assert.Equal(HttpStatusCode.Created, assignResponse.StatusCode);

        using HttpResponseMessage directoryResponse = await client.GetAsync("/v1/admin/users");
        Assert.Equal(HttpStatusCode.OK, directoryResponse.StatusCode);
        using JsonDocument directoryDoc = JsonDocument.Parse(await directoryResponse.Content.ReadAsStringAsync());
        Assert.Contains(directoryDoc.RootElement.GetProperty("items").EnumerateArray(), u => u.GetProperty("email").GetString() == email && u.GetProperty("activeCount").GetInt32() >= 1);

        using HttpResponseMessage userResponse = await client.GetAsync($"/v1/admin/users/{Uri.EscapeDataString(email)}");
        Assert.Equal(HttpStatusCode.OK, userResponse.StatusCode);
        using JsonDocument userDoc = JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync());
        Assert.Equal(email, userDoc.RootElement.GetProperty("email").GetString());
        Assert.Contains(userDoc.RootElement.GetProperty("assignments").EnumerateArray(), a => a.GetProperty("applicationId").GetString() == applicationId);
    }

    [Fact]
    public async Task ApplicationsList_SupportsSearchAndStatusFilters()
    {
        HttpClient client = factory.CreateAdminClient();
        string token = Guid.NewGuid().ToString("N");
        string applicationId = $"filter-app-{token}";
        await CreateApplicationAsync(client, applicationId);

        using HttpResponseMessage searchResponse = await client.GetAsync($"/v1/admin/applications?q={token}");
        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        using JsonDocument searchDoc = JsonDocument.Parse(await searchResponse.Content.ReadAsStringAsync());
        Assert.Contains(searchDoc.RootElement.GetProperty("items").EnumerateArray(), a => a.GetProperty("applicationId").GetString() == applicationId);

        using HttpResponseMessage statusResponse = await client.GetAsync($"/v1/admin/applications?q={token}&status=ARCHIVED");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        using JsonDocument statusDoc = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
        Assert.DoesNotContain(statusDoc.RootElement.GetProperty("items").EnumerateArray(), a => a.GetProperty("applicationId").GetString() == applicationId);
    }

    private static async Task CreateApplicationAsync(HttpClient client, string applicationId)
    {
        string tenantId = $"ten-{Guid.NewGuid():N}";
        using HttpResponseMessage tenantResponse = await client.PostAsJsonAsync("/v1/admin/tenants", new
        {
            tenantId,
            name = tenantId,
        });
        tenantResponse.EnsureSuccessStatusCode();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/admin/applications", new
        {
            applicationId,
            name = applicationId,
            tenantId,
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task CreateRoleAsync(HttpClient client, string applicationId, string roleKey)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/roles", new
        {
            roleKey,
            name = roleKey,
            privileged = false,
            riskLevel = "LOW",
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task CreatePermissionAsync(HttpClient client, string applicationId, string permissionKey)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/permissions", new
        {
            permissionKey,
            resource = permissionKey.Split('.')[0],
            action = "read",
            riskLevel = "LOW",
        });
        response.EnsureSuccessStatusCode();
    }
}
