using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Authorization.Api.Authorization;

namespace Authorization.Api.Tests.Governance;

/// <summary>
/// Covers P17 tenant-scoped delegated administration: a TenantAdmin/TenantReadOnlyViewer may
/// administer/read every application owned by their tenant, is denied on other tenants, and their
/// platform read surfaces (applications, audit, users) are filtered to their tenant server-side.
/// </summary>
public sealed class TenantScopedAdminEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public TenantScopedAdminEndpointsTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task TenantAdmin_CanManageApplicationInOwnTenant()
    {
        HttpClient admin = factory.CreateAdminClient();
        string tenantId = await CreateTenantAsync(admin);
        string applicationId = await CreateApplicationInTenantAsync(admin, tenantId);

        HttpClient tenantAdmin = factory.CreateClientForTenantRole(tenantId, DelegatedAdminRoles.TenantAdmin);
        using HttpResponseMessage response = await tenantAdmin.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/roles",
            new { roleKey = "editor", name = "Editor", privileged = false, riskLevel = "LOW" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task TenantAdmin_CannotManageApplicationInOtherTenant()
    {
        HttpClient admin = factory.CreateAdminClient();
        string ownTenantId = await CreateTenantAsync(admin);
        string otherTenantId = await CreateTenantAsync(admin);
        string otherApplicationId = await CreateApplicationInTenantAsync(admin, otherTenantId);

        HttpClient tenantAdmin = factory.CreateClientForTenantRole(ownTenantId, DelegatedAdminRoles.TenantAdmin);
        using HttpResponseMessage response = await tenantAdmin.PostAsJsonAsync(
            $"/v1/admin/applications/{otherApplicationId}/roles",
            new { roleKey = "editor", name = "Editor", privileged = false, riskLevel = "LOW" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantReadOnlyViewer_CanReadButNotMutate()
    {
        HttpClient admin = factory.CreateAdminClient();
        string tenantId = await CreateTenantAsync(admin);
        string applicationId = await CreateApplicationInTenantAsync(admin, tenantId);
        await CreateRoleAsync(admin, applicationId, "viewer-visible");

        HttpClient viewer = factory.CreateClientForTenantRole(tenantId, DelegatedAdminRoles.TenantReadOnlyViewer);

        using HttpResponseMessage read = await viewer.GetAsync($"/v1/admin/applications/{applicationId}/roles");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using HttpResponseMessage mutate = await viewer.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/roles",
            new { roleKey = "blocked", name = "Blocked", privileged = false, riskLevel = "LOW" });
        Assert.Equal(HttpStatusCode.Forbidden, mutate.StatusCode);
    }

    [Fact]
    public async Task ApplicationsList_IsScopedToTenant()
    {
        HttpClient admin = factory.CreateAdminClient();
        string ownTenantId = await CreateTenantAsync(admin);
        string ownApplicationId = await CreateApplicationInTenantAsync(admin, ownTenantId);
        string otherTenantId = await CreateTenantAsync(admin);
        string otherApplicationId = await CreateApplicationInTenantAsync(admin, otherTenantId);

        HttpClient tenantAdmin = factory.CreateClientForTenantRole(ownTenantId, DelegatedAdminRoles.TenantAdmin);
        using HttpResponseMessage response = await tenantAdmin.GetAsync("/v1/admin/applications");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        List<string> ids = doc.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("applicationId").GetString()!)
            .ToList();

        Assert.Contains(ownApplicationId, ids);
        Assert.DoesNotContain(otherApplicationId, ids);
    }

    [Fact]
    public async Task AuditFeed_IsScopedToTenant()
    {
        HttpClient admin = factory.CreateAdminClient();
        string ownTenantId = await CreateTenantAsync(admin);
        string ownApplicationId = await CreateApplicationInTenantAsync(admin, ownTenantId);
        string otherTenantId = await CreateTenantAsync(admin);
        string otherApplicationId = await CreateApplicationInTenantAsync(admin, otherTenantId);

        HttpClient tenantAdmin = factory.CreateClientForTenantRole(ownTenantId, DelegatedAdminRoles.TenantAdmin);
        using HttpResponseMessage response = await tenantAdmin.GetAsync("/v1/admin/audit-events");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        List<string?> applicationIds = doc.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("applicationId").GetString())
            .ToList();

        // Every visible event belongs to the caller's tenant (i.e. its single application).
        Assert.All(applicationIds, id => Assert.Equal(ownApplicationId, id));
        Assert.DoesNotContain(otherApplicationId, applicationIds);
    }

    [Fact]
    public async Task UsersDirectory_IsScopedToTenant()
    {
        HttpClient admin = factory.CreateAdminClient();
        string ownTenantId = await CreateTenantAsync(admin);
        string ownApplicationId = await CreateApplicationInTenantAsync(admin, ownTenantId);
        await CreateRoleAsync(admin, ownApplicationId, "own-role");
        await CreateAssignmentAsync(admin, ownApplicationId, "tenant-a-user@local.test", "own-role");

        string otherTenantId = await CreateTenantAsync(admin);
        string otherApplicationId = await CreateApplicationInTenantAsync(admin, otherTenantId);
        await CreateRoleAsync(admin, otherApplicationId, "other-role");
        await CreateAssignmentAsync(admin, otherApplicationId, "tenant-b-user@local.test", "other-role");

        HttpClient tenantAdmin = factory.CreateClientForTenantRole(ownTenantId, DelegatedAdminRoles.TenantAdmin);
        using HttpResponseMessage response = await tenantAdmin.GetAsync("/v1/admin/users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        List<string> emails = doc.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("email").GetString()!)
            .ToList();

        Assert.Contains("tenant-a-user@local.test", emails);
        Assert.DoesNotContain("tenant-b-user@local.test", emails);
    }

    private static async Task CreateRoleAsync(HttpClient client, string applicationId, string roleKey)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/roles",
            new { roleKey, name = roleKey, privileged = false, riskLevel = "LOW" });
        response.EnsureSuccessStatusCode();
    }

    private static async Task CreateAssignmentAsync(HttpClient client, string applicationId, string subjectEmail, string roleKey)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/assignments/import",
            new
            {
                dryRun = false,
                rows = new object[] { new { subjectEmail, roleKey } },
            });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> CreateTenantAsync(HttpClient client)
    {
        string tenantId = $"ten-{Guid.NewGuid():N}";
        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/admin/tenants", new
        {
            tenantId,
            name = tenantId,
        });
        response.EnsureSuccessStatusCode();
        return tenantId;
    }

    private static async Task<string> CreateApplicationInTenantAsync(HttpClient client, string tenantId)
    {
        string applicationId = $"tsa-app-{Guid.NewGuid():N}";
        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/admin/applications", new
        {
            applicationId,
            name = applicationId,
            tenantId,
        });
        response.EnsureSuccessStatusCode();
        return applicationId;
    }
}
