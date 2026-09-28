using System.Net;
using System.Net.Http.Json;
using Authorization.Api.Authorization;

namespace Authorization.Api.Tests.Admin;

public sealed class DelegatedAdminEnforcementTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public DelegatedAdminEnforcementTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task AppScopedManager_CanManageOwnApplication_ButNotOtherApplications()
    {
        HttpClient platformClient = factory.CreateAdminClient();
        string ownApplicationId = $"own-app-{Guid.NewGuid():N}";
        string otherApplicationId = $"other-app-{Guid.NewGuid():N}";
        await CreateApplicationAsync(platformClient, ownApplicationId);
        await CreateApplicationAsync(platformClient, otherApplicationId);

        HttpClient scopedClient = factory.CreateClientForApplicationRole(ownApplicationId, DelegatedAdminRoles.ApplicationAdmin);

        using HttpResponseMessage ownResponse = await scopedClient.PostAsJsonAsync(
            $"/v1/admin/applications/{ownApplicationId}/roles",
            new { roleKey = "reviewer", name = "Reviewer", privileged = false, riskLevel = "LOW" });
        using HttpResponseMessage crossResponse = await scopedClient.PostAsJsonAsync(
            $"/v1/admin/applications/{otherApplicationId}/roles",
            new { roleKey = "reviewer", name = "Reviewer", privileged = false, riskLevel = "LOW" });

        Assert.Equal(HttpStatusCode.Created, ownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossResponse.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedUserWithoutDelegatedRole_IsForbiddenFromReadAndWrite()
    {
        HttpClient platformClient = factory.CreateAdminClient();
        string applicationId = $"norole-app-{Guid.NewGuid():N}";
        await CreateApplicationAsync(platformClient, applicationId);

        HttpClient rolelessClient = factory.CreateAuthenticatedClientWithoutRoles();

        using HttpResponseMessage readResponse = await rolelessClient.GetAsync($"/v1/admin/applications/{applicationId}/roles");
        using HttpResponseMessage writeResponse = await rolelessClient.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/roles",
            new { roleKey = "reviewer", name = "Reviewer", privileged = false, riskLevel = "LOW" });

        Assert.Equal(HttpStatusCode.Forbidden, readResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, writeResponse.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyViewer_CanReadButCannotMutate()
    {
        HttpClient platformClient = factory.CreateAdminClient();
        string applicationId = $"readonly-app-{Guid.NewGuid():N}";
        await CreateApplicationAsync(platformClient, applicationId);

        HttpClient viewerClient = factory.CreateClientForApplicationRole(applicationId, DelegatedAdminRoles.ReadOnlyViewer);

        using HttpResponseMessage readResponse = await viewerClient.GetAsync($"/v1/admin/applications/{applicationId}/roles");
        using HttpResponseMessage writeResponse = await viewerClient.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/roles",
            new { roleKey = "reviewer", name = "Reviewer", privileged = false, riskLevel = "LOW" });

        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, writeResponse.StatusCode);
    }

    [Fact]
    public async Task PlatformReadOnlyViewer_CanReadAnyApplication_ButCannotMutate()
    {
        HttpClient platformClient = factory.CreateAdminClient();
        string applicationId = $"platform-readonly-app-{Guid.NewGuid():N}";
        await CreateApplicationAsync(platformClient, applicationId);

        HttpClient viewerClient = factory.CreateClientForPlatformRole(DelegatedAdminRoles.PlatformReadOnlyViewer);

        using HttpResponseMessage readResponse = await viewerClient.GetAsync($"/v1/admin/applications/{applicationId}/roles");
        using HttpResponseMessage writeResponse = await viewerClient.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/roles",
            new { roleKey = "reviewer", name = "Reviewer", privileged = false, riskLevel = "LOW" });

        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, writeResponse.StatusCode);
    }

    [Fact]
    public async Task DraftRoleMapping_CanBePublished_AndBecomesPublished()
    {
        HttpClient platformClient = factory.CreateAdminClient();
        string applicationId = $"publish-app-{Guid.NewGuid():N}";
        await CreateApplicationAsync(platformClient, applicationId);
        await platformClient.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/roles",
            new { roleKey = "reviewer", name = "Reviewer", privileged = false, riskLevel = "LOW" });
        await platformClient.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/permissions",
            new { permissionKey = "invoice.view", resource = "invoice", action = "view", riskLevel = "LOW" });

        using HttpResponseMessage createResponse = await platformClient.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/role-permissions",
            new { roleKey = "reviewer", permissionKey = "invoice.view", publish = false });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<RolePermissionResponse>();
        Assert.NotNull(created);
        Assert.Equal("DRAFT", created!.State);

        using HttpResponseMessage publishResponse = await platformClient.PostAsync(
            $"/v1/admin/applications/{applicationId}/role-permissions/{created.Id}/publish", content: null);
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        var published = await publishResponse.Content.ReadFromJsonAsync<RolePermissionResponse>();
        Assert.NotNull(published);
        Assert.Equal("PUBLISHED", published!.State);
        Assert.NotNull(published.PublishedAt);
    }

    [Fact]
    public async Task PublishRoleMapping_IsForbidden_ForCrossApplicationScopedManager()
    {
        HttpClient platformClient = factory.CreateAdminClient();
        string ownApplicationId = $"pub-own-{Guid.NewGuid():N}";
        string otherApplicationId = $"pub-other-{Guid.NewGuid():N}";
        await CreateApplicationAsync(platformClient, ownApplicationId);
        await CreateApplicationAsync(platformClient, otherApplicationId);

        using HttpResponseMessage createResponse = await platformClient.PostAsJsonAsync(
            $"/v1/admin/applications/{otherApplicationId}/role-permissions",
            new { roleKey = "reviewer", permissionKey = "invoice.view", publish = false });
        var created = await createResponse.Content.ReadFromJsonAsync<RolePermissionResponse>();
        Assert.NotNull(created);

        HttpClient scopedClient = factory.CreateClientForApplicationRole(ownApplicationId, DelegatedAdminRoles.ApplicationAdmin);
        using HttpResponseMessage publishResponse = await scopedClient.PostAsync(
            $"/v1/admin/applications/{otherApplicationId}/role-permissions/{created!.Id}/publish", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, publishResponse.StatusCode);
    }

    [Fact]
    public async Task RoleMapping_CanBeDeleted_AndDisappearsFromList()
    {
        HttpClient platformClient = factory.CreateAdminClient();
        string applicationId = $"unmap-app-{Guid.NewGuid():N}";
        await CreateApplicationAsync(platformClient, applicationId);
        await platformClient.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/roles",
            new { roleKey = "reviewer", name = "Reviewer", privileged = false, riskLevel = "LOW" });
        await platformClient.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/permissions",
            new { permissionKey = "invoice.view", resource = "invoice", action = "view", riskLevel = "LOW" });

        using HttpResponseMessage createResponse = await platformClient.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/role-permissions",
            new { roleKey = "reviewer", permissionKey = "invoice.view", publish = true });
        var created = await createResponse.Content.ReadFromJsonAsync<RolePermissionResponse>();
        Assert.NotNull(created);

        using HttpResponseMessage deleteResponse = await platformClient.DeleteAsync(
            $"/v1/admin/applications/{applicationId}/role-permissions/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using HttpResponseMessage listResponse = await platformClient.GetAsync(
            $"/v1/admin/applications/{applicationId}/role-permissions");
        var remaining = await listResponse.Content.ReadFromJsonAsync<List<RolePermissionResponse>>();
        Assert.NotNull(remaining);
        Assert.DoesNotContain(remaining!, m => m.Id == created.Id);
    }

    [Fact]
    public async Task AuditEvents_AreReadableForOwnApplication_ButForbiddenForOtherApplications()
    {
        HttpClient platformClient = factory.CreateAdminClient();
        string ownApplicationId = $"audit-own-{Guid.NewGuid():N}";
        string otherApplicationId = $"audit-other-{Guid.NewGuid():N}";
        await CreateApplicationAsync(platformClient, ownApplicationId);
        await CreateApplicationAsync(platformClient, otherApplicationId);

        HttpClient auditorClient = factory.CreateClientForApplicationRole(ownApplicationId, DelegatedAdminRoles.ReadOnlyViewer);

        using HttpResponseMessage ownResponse = await auditorClient.GetAsync($"/v1/admin/audit-events?applicationId={ownApplicationId}");
        using HttpResponseMessage crossResponse = await auditorClient.GetAsync($"/v1/admin/audit-events?applicationId={otherApplicationId}");

        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossResponse.StatusCode);
    }

    [Fact]
    public async Task AuditEvents_WithoutApplicationFilter_AreScopedToAuditableApplications()
    {
        HttpClient platformClient = factory.CreateAdminClient();
        string ownApplicationId = $"audit-scope-own-{Guid.NewGuid():N}";
        string otherApplicationId = $"audit-scope-other-{Guid.NewGuid():N}";
        await CreateApplicationAsync(platformClient, ownApplicationId);
        await CreateApplicationAsync(platformClient, otherApplicationId);

        HttpClient auditorClient = factory.CreateClientForApplicationRole(ownApplicationId, DelegatedAdminRoles.ReadOnlyViewer);

        using HttpResponseMessage response = await auditorClient.GetAsync("/v1/admin/audit-events");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await response.Content.ReadFromJsonAsync<PagedView<AuditEventView>>();
        Assert.NotNull(events);
        Assert.All(events!.Items, e => Assert.Equal(ownApplicationId, e.ApplicationId));
    }

    [Fact]
    public async Task Simulator_IsAllowedForOwnApplication_ButForbiddenForOtherApplications()
    {
        HttpClient platformClient = factory.CreateAdminClient();
        string ownApplicationId = $"sim-own-{Guid.NewGuid():N}";
        string otherApplicationId = $"sim-other-{Guid.NewGuid():N}";
        await CreateApplicationAsync(platformClient, ownApplicationId);
        await CreateApplicationAsync(platformClient, otherApplicationId);

        HttpClient viewerClient = factory.CreateClientForApplicationRole(ownApplicationId, DelegatedAdminRoles.ReadOnlyViewer);

        using HttpResponseMessage ownResponse = await viewerClient.PostAsJsonAsync("/v1/admin/simulator/authorize", SimulatorRequest(ownApplicationId));
        using HttpResponseMessage crossResponse = await viewerClient.PostAsJsonAsync("/v1/admin/simulator/authorize", SimulatorRequest(otherApplicationId));

        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossResponse.StatusCode);
    }

    private static object SimulatorRequest(string applicationId) => new
    {
        applicationId,
        subjectType = "USER",
        subjectEmail = "subject@local.test",
        resourceType = "invoice",
        resourceId = "invoice-1",
        action = "read",
        context = new Dictionary<string, object?>(),
    };

    private sealed record AuditEventView(string? ApplicationId);

    private sealed record PagedView<T>(List<T> Items, int Page, int PageSize, int Total);

    [Fact]
    public async Task Tenants_AreScopedToAccessibleTenants_ForApplicationScopedAdmin()
    {
        HttpClient platformClient = factory.CreateAdminClient();
        string ownApplicationId = $"tenant-own-app-{Guid.NewGuid():N}";
        string otherApplicationId = $"tenant-other-app-{Guid.NewGuid():N}";
        string ownTenantId = await CreateApplicationAsync(platformClient, ownApplicationId);
        string otherTenantId = await CreateApplicationAsync(platformClient, otherApplicationId);

        HttpClient scopedClient = factory.CreateClientForApplicationRole(ownApplicationId, DelegatedAdminRoles.ApplicationAdmin);

        using HttpResponseMessage response = await scopedClient.GetAsync("/v1/admin/tenants");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tenants = await response.Content.ReadFromJsonAsync<List<TenantView>>();
        Assert.NotNull(tenants);
        Assert.Contains(tenants!, t => t.TenantId == ownTenantId);
        Assert.DoesNotContain(tenants!, t => t.TenantId == otherTenantId);
    }

    [Fact]
    public async Task TenantDetail_ForInaccessibleTenant_IsNotFound_ForApplicationScopedAdmin()
    {
        HttpClient platformClient = factory.CreateAdminClient();
        string ownApplicationId = $"tenant-detail-own-{Guid.NewGuid():N}";
        string otherApplicationId = $"tenant-detail-other-{Guid.NewGuid():N}";
        string ownTenantId = await CreateApplicationAsync(platformClient, ownApplicationId);
        string otherTenantId = await CreateApplicationAsync(platformClient, otherApplicationId);

        HttpClient scopedClient = factory.CreateClientForApplicationRole(ownApplicationId, DelegatedAdminRoles.ApplicationAdmin);

        using HttpResponseMessage ownResponse = await scopedClient.GetAsync($"/v1/admin/tenants/{ownTenantId}");
        using HttpResponseMessage crossResponse = await scopedClient.GetAsync($"/v1/admin/tenants/{otherTenantId}");

        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossResponse.StatusCode);
    }

    [Fact]
    public async Task PlatformReadOnlyViewer_CanListAllTenants()
    {
        HttpClient platformClient = factory.CreateAdminClient();
        string applicationId = $"tenant-platform-{Guid.NewGuid():N}";
        string tenantId = await CreateApplicationAsync(platformClient, applicationId);

        HttpClient viewerClient = factory.CreateClientForPlatformRole(DelegatedAdminRoles.PlatformReadOnlyViewer);

        using HttpResponseMessage response = await viewerClient.GetAsync("/v1/admin/tenants");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tenants = await response.Content.ReadFromJsonAsync<List<TenantView>>();
        Assert.NotNull(tenants);
        Assert.Contains(tenants!, t => t.TenantId == tenantId);
    }

    private sealed record TenantView(string TenantId, string Name);

    private sealed record RolePermissionResponse(Guid Id, string State, DateTimeOffset? PublishedAt);

    private static async Task<string> CreateApplicationAsync(HttpClient client, string applicationId)
    {
        string tenantId = $"tenant-{Guid.NewGuid():N}";
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
        return tenantId;
    }
}
