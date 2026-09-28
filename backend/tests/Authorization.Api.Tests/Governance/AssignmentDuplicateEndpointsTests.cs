using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Authorization.Api.Tests.Governance;

/// <summary>
/// Covers the de-duplication rules shared by manual create, edit and CSV import: a subject either
/// actively holds a role or it does not; an exact duplicate is rejected/skipped, and a differing
/// expiry consolidates onto the single active grant (keeping the later expiry) rather than creating
/// a second record. Revoked/expired history never blocks a re-grant.
/// </summary>
public sealed class AssignmentDuplicateEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public AssignmentDuplicateEndpointsTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Create_ExactActiveDuplicate_ReturnsConflict()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(client);
        await CreateRoleAsync(client, applicationId, "member");
        string expiry = DateTimeOffset.UtcNow.AddDays(10).ToString("O");

        using HttpResponseMessage first = await CreateAssignmentAsync(client, applicationId, "dup@local.test", "member", expiry);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using HttpResponseMessage second = await CreateAssignmentAsync(client, applicationId, "dup@local.test", "member", expiry);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        Assert.Equal(1, await CountAssignmentsAsync(client, applicationId, "dup@local.test"));
    }

    [Fact]
    public async Task Create_DifferingExpiry_ConsolidatesOntoExistingGrant()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(client);
        await CreateRoleAsync(client, applicationId, "member");
        string sooner = DateTimeOffset.UtcNow.AddDays(10).ToString("O");
        DateTimeOffset laterExpiry = DateTimeOffset.UtcNow.AddDays(30);

        using (HttpResponseMessage first = await CreateAssignmentAsync(client, applicationId, "grow@local.test", "member", sooner))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        // Re-granting with a later expiry consolidates (200 OK) instead of creating a second row.
        using (HttpResponseMessage second = await CreateAssignmentAsync(client, applicationId, "grow@local.test", "member", laterExpiry.ToString("O")))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        Assert.Equal(1, await CountAssignmentsAsync(client, applicationId, "grow@local.test"));
    }

    [Fact]
    public async Task Import_SkipsDbDuplicate_AndUpdatesOnLaterExpiry()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(client);
        await CreateRoleAsync(client, applicationId, "member");
        string expiry = DateTimeOffset.UtcNow.AddDays(10).ToString("O");
        string later = DateTimeOffset.UtcNow.AddDays(40).ToString("O");
        await CreateAssignmentAsync(client, applicationId, "person@local.test", "member", expiry);

        // Same expiry → skipped; later expiry → update.
        using HttpResponseMessage sameExpiry = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/assignments/import",
            new { dryRun = true, rows = new[] { new { subjectEmail = "person@local.test", roleKey = "member", validUntil = expiry } } });
        using (JsonDocument doc = JsonDocument.Parse(await sameExpiry.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("skipped").GetInt32());
            Assert.Equal(0, doc.RootElement.GetProperty("created").GetInt32());
            Assert.Equal(0, doc.RootElement.GetProperty("updated").GetInt32());
        }

        using HttpResponseMessage laterExpiry = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/assignments/import",
            new { dryRun = true, rows = new[] { new { subjectEmail = "person@local.test", roleKey = "member", validUntil = later } } });
        using (JsonDocument doc = JsonDocument.Parse(await laterExpiry.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("updated").GetInt32());
            Assert.Equal(0, doc.RootElement.GetProperty("created").GetInt32());
        }
    }

    [Fact]
    public async Task Import_FoldsInFileDuplicates()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(client);
        await CreateRoleAsync(client, applicationId, "member");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/assignments/import",
            new
            {
                dryRun = false,
                rows = new[]
                {
                    new { subjectEmail = "twice@local.test", roleKey = "member" },
                    new { subjectEmail = "twice@local.test", roleKey = "member" },
                },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("created").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("skipped").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("applied").GetInt32());
        Assert.Equal(1, await CountAssignmentsAsync(client, applicationId, "twice@local.test"));
    }

    [Fact]
    public async Task Create_AfterRevoke_IsAllowedAgain()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(client);
        await CreateRoleAsync(client, applicationId, "member");

        Guid assignmentId;
        using (HttpResponseMessage first = await CreateAssignmentAsync(client, applicationId, "rehire@local.test", "member", null))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            using JsonDocument doc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            assignmentId = doc.RootElement.GetProperty("id").GetGuid();
        }

        using (HttpResponseMessage revoke = await client.PostAsync(
            $"/v1/admin/applications/{applicationId}/assignments/{assignmentId}/revoke", content: null))
        {
            revoke.EnsureSuccessStatusCode();
        }

        // A revoked grant is history — the same subject+role can be granted again.
        using HttpResponseMessage regrant = await CreateAssignmentAsync(client, applicationId, "rehire@local.test", "member", null);
        Assert.Equal(HttpStatusCode.Created, regrant.StatusCode);
    }

    private static Task<HttpResponseMessage> CreateAssignmentAsync(HttpClient client, string applicationId, string email, string roleKey, string? validUntil)
    {
        return client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/assignments",
            new { subjectType = "USER", subjectEmail = email, roleKey, validUntil, source = "MANUAL", privileged = false });
    }

    private static async Task<int> CountAssignmentsAsync(HttpClient client, string applicationId, string email)
    {
        using HttpResponseMessage list = await client.GetAsync($"/v1/admin/applications/{applicationId}/assignments");
        using JsonDocument doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Count(r => string.Equals(r.GetProperty("subjectEmail").GetString(), email, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task CreateRoleAsync(HttpClient client, string applicationId, string roleKey)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/roles",
            new { roleKey, name = roleKey, privileged = false, riskLevel = "LOW" });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> CreateApplicationAsync(HttpClient client)
    {
        string tenantId = $"ten-{Guid.NewGuid():N}";
        using HttpResponseMessage tenantResponse = await client.PostAsJsonAsync("/v1/admin/tenants", new { tenantId, name = tenantId });
        tenantResponse.EnsureSuccessStatusCode();

        string applicationId = $"dup-app-{Guid.NewGuid():N}";
        using HttpResponseMessage appResponse = await client.PostAsJsonAsync("/v1/admin/applications", new { applicationId, name = applicationId, tenantId });
        appResponse.EnsureSuccessStatusCode();
        return applicationId;
    }
}
