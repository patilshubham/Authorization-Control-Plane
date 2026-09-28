using System.Net;
using System.Net.Http.Json;

namespace Authorization.Api.Tests.Admin;

public sealed class AuditFeedTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public AuditFeedTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Feed_FiltersByCategory()
    {
        HttpClient client = factory.CreateAdminClient();
        string appId = $"audit-cat-{Guid.NewGuid():N}";
        await SeedAsync(client, appId);

        using HttpResponseMessage response =
            await client.GetAsync($"/v1/admin/audit-events?applicationId={appId}&category=Permission");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<Paged<FeedEvent>>();
        Assert.NotNull(page);
        Assert.NotEmpty(page!.Items);
        Assert.All(page.Items, e => Assert.StartsWith("PERMISSION", e.EventType));
    }

    [Fact]
    public async Task Feed_FiltersByFreeTextQuery()
    {
        HttpClient client = factory.CreateAdminClient();
        string appId = $"audit-q-{Guid.NewGuid():N}";
        await SeedAsync(client, appId);

        using HttpResponseMessage response =
            await client.GetAsync($"/v1/admin/audit-events?applicationId={appId}&q=ROLE_CREATED");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<Paged<FeedEvent>>();
        Assert.NotNull(page);
        Assert.NotEmpty(page!.Items);
        Assert.All(page.Items, e => Assert.Equal("ROLE_CREATED", e.EventType));
    }

    [Fact]
    public async Task Feed_PaginatesResults()
    {
        HttpClient client = factory.CreateAdminClient();
        string appId = $"audit-page-{Guid.NewGuid():N}";
        await SeedAsync(client, appId);

        using HttpResponseMessage response =
            await client.GetAsync($"/v1/admin/audit-events?applicationId={appId}&page=1&pageSize=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<Paged<FeedEvent>>();
        Assert.NotNull(page);
        Assert.Single(page!.Items);
        Assert.Equal(1, page.Page);
        Assert.Equal(1, page.PageSize);
        Assert.True(page.Total >= 2);
    }

    [Fact]
    public async Task Summary_ReturnsPerDayCounts()
    {
        HttpClient client = factory.CreateAdminClient();
        string appId = $"audit-sum-{Guid.NewGuid():N}";
        await SeedAsync(client, appId);

        using HttpResponseMessage response =
            await client.GetAsync($"/v1/admin/audit-events/summary?applicationId={appId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<Summary>();
        Assert.NotNull(summary);
        Assert.True(summary!.Total >= 2);
        Assert.NotEmpty(summary.Days);
        Assert.All(summary.Days, d => Assert.True(d.Count > 0));
        string today = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(summary.Days, d => d.Date == today);
    }

    // Creates an application plus a role and permission so at least two audit events
    // (ROLE_CREATED, PERMISSION_CREATED) exist for the application, spanning two categories.
    private static async Task SeedAsync(HttpClient client, string applicationId)
    {
        string tenantId = $"tenant-{Guid.NewGuid():N}";
        using (HttpResponseMessage tenant = await client.PostAsJsonAsync("/v1/admin/tenants", new { tenantId, name = tenantId }))
            tenant.EnsureSuccessStatusCode();
        using (HttpResponseMessage app = await client.PostAsJsonAsync("/v1/admin/applications", new { applicationId, name = applicationId, tenantId }))
            app.EnsureSuccessStatusCode();
        using (HttpResponseMessage role = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/roles",
            new { roleKey = "reviewer", name = "Reviewer", privileged = false, riskLevel = "LOW" }))
            role.EnsureSuccessStatusCode();
        using (HttpResponseMessage permission = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/permissions",
            new { permissionKey = "invoice.approve", resource = "invoice", action = "approve", riskLevel = "LOW" }))
            permission.EnsureSuccessStatusCode();
    }

    private sealed record FeedEvent(string EventType, string? ApplicationId);

    private sealed record Paged<T>(List<T> Items, int Page, int PageSize, int Total);

    private sealed record SummaryDay(string Date, int Count);

    private sealed record Summary(int Total, List<SummaryDay> Days);
}
