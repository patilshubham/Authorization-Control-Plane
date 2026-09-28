using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Authorization.Api.Tests.Governance;

/// <summary>
/// Covers the P9 bulk CSV surfaces: assignment export (CSV file) and import with a
/// validate-only dry-run plus an audited apply (Source=IMPORT).
/// </summary>
public sealed class AssignmentImportExportEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public AssignmentImportExportEndpointsTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Export_ReturnsCsvWithHeader()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(client);

        using HttpResponseMessage response = await client.GetAsync(
            $"/v1/admin/applications/{applicationId}/assignments/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        string csv = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("subjectEmail,roleKey,state,validFrom,validUntil,source,reason", csv);
    }

    [Fact]
    public async Task Import_DryRun_ValidatesWithoutPersisting()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(client);
        await CreateRoleAsync(client, applicationId, "importer");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/assignments/import",
            new
            {
                dryRun = true,
                rows = new object[]
                {
                    new { subjectEmail = "a@local.test", roleKey = "importer" },
                    new { subjectEmail = "b@local.test", roleKey = "does-not-exist" },
                    new { subjectEmail = "", roleKey = "importer" },
                },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("dryRun").GetBoolean());
        Assert.Equal(3, root.GetProperty("total").GetInt32());
        Assert.Equal(1, root.GetProperty("valid").GetInt32());
        Assert.Equal(2, root.GetProperty("failed").GetInt32());
        Assert.Equal(0, root.GetProperty("applied").GetInt32());

        // Nothing was persisted by a dry-run.
        using HttpResponseMessage list = await client.GetAsync(
            $"/v1/admin/applications/{applicationId}/assignments");
        using JsonDocument listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal(0, listDoc.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Import_Apply_CreatesAssignments()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(client);
        await CreateRoleAsync(client, applicationId, "importer");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/assignments/import",
            new
            {
                dryRun = false,
                rows = new object[]
                {
                    new { subjectEmail = "a@local.test", roleKey = "importer" },
                    new { subjectEmail = "c@local.test", roleKey = "importer" },
                },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("applied").GetInt32());

        using HttpResponseMessage list = await client.GetAsync(
            $"/v1/admin/applications/{applicationId}/assignments");
        using JsonDocument listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal(2, listDoc.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Import_ForbiddenForReadOnlyRole()
    {
        HttpClient admin = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(admin);
        await CreateRoleAsync(admin, applicationId, "importer");

        HttpClient readOnly = factory.CreateClientForApplicationRole(applicationId, "ReadOnlyViewer");
        using HttpResponseMessage response = await readOnly.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/assignments/import",
            new
            {
                dryRun = false,
                rows = new object[] { new { subjectEmail = "a@local.test", roleKey = "importer" } },
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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
        using HttpResponseMessage tenantResponse = await client.PostAsJsonAsync("/v1/admin/tenants", new
        {
            tenantId,
            name = tenantId,
        });
        tenantResponse.EnsureSuccessStatusCode();

        string applicationId = $"imp-app-{Guid.NewGuid():N}";
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
