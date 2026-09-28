using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Authorization.Api.Tests.Governance;

/// <summary>
/// Covers the P14 certification campaign lifecycle: create (DRAFT) → activate (snapshots active
/// assignments into items) → per-item decisions → finalize (applies approved REVOKEs via the
/// audited revoke path and closes the campaign).
/// </summary>
public sealed class ReviewCampaignEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public ReviewCampaignEndpointsTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Lifecycle_Create_Activate_Decide_Finalize_AppliesRevokes()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(client);
        await CreateRoleAsync(client, applicationId, "reviewer");
        await AssignAsync(client, applicationId, "keep@local.test", "reviewer");
        await AssignAsync(client, applicationId, "revoke@local.test", "reviewer");

        // Create → DRAFT
        Guid campaignId;
        using (HttpResponseMessage create = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/review-campaigns",
            new { name = "Quarterly review" }))
        {
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
            using JsonDocument doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
            Assert.Equal("DRAFT", doc.RootElement.GetProperty("status").GetString());
            campaignId = doc.RootElement.GetProperty("id").GetGuid();
        }

        // Activate → items generated from the two active assignments
        Dictionary<string, Guid> itemIdByEmail = new();
        using (HttpResponseMessage activate = await client.PostAsync(
            $"/v1/admin/applications/{applicationId}/review-campaigns/{campaignId}/activate", content: null))
        {
            Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
            using JsonDocument doc = JsonDocument.Parse(await activate.Content.ReadAsStringAsync());
            Assert.Equal("ACTIVE", doc.RootElement.GetProperty("campaign").GetProperty("status").GetString());
            JsonElement items = doc.RootElement.GetProperty("items");
            Assert.Equal(2, items.GetArrayLength());
            foreach (JsonElement item in items.EnumerateArray())
            {
                itemIdByEmail[item.GetProperty("subjectEmail").GetString()!] = item.GetProperty("id").GetGuid();
            }
        }

        // Decide: keep one, revoke the other
        await DecideAsync(client, applicationId, campaignId, itemIdByEmail["keep@local.test"], "KEEP");
        await DecideAsync(client, applicationId, campaignId, itemIdByEmail["revoke@local.test"], "REVOKE");

        // Finalize → campaign CLOSED, the REVOKE assignment revoked
        using (HttpResponseMessage finalize = await client.PostAsync(
            $"/v1/admin/applications/{applicationId}/review-campaigns/{campaignId}/finalize", content: null))
        {
            Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
            using JsonDocument doc = JsonDocument.Parse(await finalize.Content.ReadAsStringAsync());
            Assert.Equal("CLOSED", doc.RootElement.GetProperty("campaign").GetProperty("status").GetString());
        }

        // The revoked subject's assignment is now REVOKED; the kept one stays ACTIVE.
        using HttpResponseMessage list = await client.GetAsync(
            $"/v1/admin/applications/{applicationId}/assignments");
        using JsonDocument listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        JsonElement[] rows = listDoc.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(
            "REVOKED",
            rows.Single(r => r.GetProperty("subjectEmail").GetString() == "revoke@local.test").GetProperty("state").GetString());
        Assert.Equal(
            "ACTIVE",
            rows.Single(r => r.GetProperty("subjectEmail").GetString() == "keep@local.test").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Create_ForbiddenForReadOnlyRole()
    {
        HttpClient admin = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(admin);

        HttpClient readOnly = factory.CreateClientForApplicationRole(applicationId, "ReadOnlyViewer");
        using HttpResponseMessage response = await readOnly.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/review-campaigns",
            new { name = "Nope" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task BulkDecision_WithNoItemIds_KeepsAllPending()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(client);
        await CreateRoleAsync(client, applicationId, "reviewer");
        await AssignAsync(client, applicationId, "a@local.test", "reviewer");
        await AssignAsync(client, applicationId, "b@local.test", "reviewer");

        Guid campaignId;
        using (HttpResponseMessage create = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/review-campaigns",
            new { name = "Bulk review" }))
        {
            using JsonDocument doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
            campaignId = doc.RootElement.GetProperty("id").GetGuid();
        }

        using (HttpResponseMessage activate = await client.PostAsync(
            $"/v1/admin/applications/{applicationId}/review-campaigns/{campaignId}/activate", content: null))
        {
            activate.EnsureSuccessStatusCode();
        }

        // Keep all remaining (no explicit ids) — with a shared note.
        using HttpResponseMessage bulk = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/review-campaigns/{campaignId}/decisions",
            new { decision = "KEEP", note = "Bulk kept after review" });
        Assert.Equal(HttpStatusCode.OK, bulk.StatusCode);

        using JsonDocument bulkDoc = JsonDocument.Parse(await bulk.Content.ReadAsStringAsync());
        JsonElement campaign = bulkDoc.RootElement.GetProperty("campaign");
        Assert.Equal(0, campaign.GetProperty("pendingCount").GetInt32());
        Assert.Equal(2, campaign.GetProperty("keepCount").GetInt32());
        Assert.All(
            bulkDoc.RootElement.GetProperty("items").EnumerateArray(),
            item => Assert.Equal("KEEP", item.GetProperty("decision").GetString()));
    }

    private static async Task DecideAsync(HttpClient client, string applicationId, Guid campaignId, Guid itemId, string decision)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/review-campaigns/{campaignId}/items/{itemId}/decision",
            new { decision });
        response.EnsureSuccessStatusCode();
    }

    private static async Task AssignAsync(HttpClient client, string applicationId, string email, string roleKey)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/assignments",
            new { subjectType = "USER", subjectEmail = email, roleKey });
        response.EnsureSuccessStatusCode();
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

        string applicationId = $"cert-app-{Guid.NewGuid():N}";
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
