using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Authorization.Api.Tests.Governance;

/// <summary>
/// Covers the P15 break-glass (emergency) access surface: a short, mandatory-reason
/// EMERGENCY grant that auto-expires and is recorded as a high-visibility audit event.
/// </summary>
public sealed class BreakGlassEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public BreakGlassEndpointsTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task BreakGlass_CreatesTimeBoxedEmergencyGrant_AndAuditsLoudly()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(client);
        await CreateRoleAsync(client, applicationId, "responder");

        DateTimeOffset before = DateTimeOffset.UtcNow;
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/assignments/break-glass",
            new
            {
                subjectEmail = "oncall@local.test",
                roleKey = "responder",
                reason = "INC-4210 production outage",
                durationHours = 4,
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // The grant is an EMERGENCY assignment carrying the justification and an expiry
        // roughly durationHours in the future.
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthorizationDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        var assignment = await dbContext.Assignments
            .SingleAsync(a => a.SubjectEmail == "oncall@local.test");
        Assert.Equal("EMERGENCY", assignment.Source);
        Assert.Equal("INC-4210 production outage", assignment.Reason);
        Assert.NotNull(assignment.ValidUntil);
        Assert.True(assignment.ValidUntil > before.AddHours(3));
        Assert.True(assignment.ValidUntil <= before.AddHours(4).AddMinutes(1));

        // A distinct, high-visibility audit event is written.
        Assert.True(await dbContext.AuditEvents.AnyAsync(e =>
            e.ApplicationId == applicationId
            && e.EventType == "BREAK_GLASS_ACTIVATED"
            && e.TargetSubjectEmail == "oncall@local.test"));
    }

    [Fact]
    public async Task BreakGlass_RequiresJustification()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(client);
        await CreateRoleAsync(client, applicationId, "responder");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/assignments/break-glass",
            new
            {
                subjectEmail = "oncall@local.test",
                roleKey = "responder",
                reason = "   ",
                durationHours = 2,
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task BreakGlass_RejectsDurationOutsideBounds()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(client);
        await CreateRoleAsync(client, applicationId, "responder");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/assignments/break-glass",
            new
            {
                subjectEmail = "oncall@local.test",
                roleKey = "responder",
                reason = "INC-1",
                durationHours = 0,
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task BreakGlass_ForbiddenForReadOnlyRole()
    {
        HttpClient admin = factory.CreateAdminClient();
        string applicationId = await CreateApplicationAsync(admin);
        await CreateRoleAsync(admin, applicationId, "responder");

        HttpClient readOnly = factory.CreateClientForApplicationRole(applicationId, "ReadOnlyViewer");
        using HttpResponseMessage response = await readOnly.PostAsJsonAsync(
            $"/v1/admin/applications/{applicationId}/assignments/break-glass",
            new
            {
                subjectEmail = "oncall@local.test",
                roleKey = "responder",
                reason = "INC-1",
                durationHours = 2,
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

        string applicationId = $"bg-app-{Guid.NewGuid():N}";
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
