using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Authorization.Api.Authorization;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Authorization.Api.Tests.Admin;

/// <summary>
/// End-to-end regression covering the simplified role model (PlatformSuperAdmin, PlatformReadOnlyViewer,
/// ApplicationAdmin, ReadOnlyViewer) plus an unauthenticated-role baseline. Verifies both HTTP
/// authorization outcomes and that create/update/delete operations are actually persisted.
/// </summary>
public sealed class RoleMatrixRegressionTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public RoleMatrixRegressionTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task ApplicationAdmin_FullLifecycle_PersistsCreateUpdateDeleteInDatabase()
    {
        HttpClient platform = factory.CreateAdminClient();
        string appId = $"lifecycle-{Guid.NewGuid():N}";
        await CreateApplicationAsync(platform, appId);

        HttpClient admin = factory.CreateClientForApplicationRole(appId, DelegatedAdminRoles.ApplicationAdmin);

        // ── ROLE: create ────────────────────────────────────────────────────────
        using HttpResponseMessage createRole = await admin.PostAsJsonAsync(
            $"/v1/admin/applications/{appId}/roles",
            new { roleKey = "approver", name = "Approver", description = "Approves invoices", privileged = false, riskLevel = "HIGH" });
        Assert.Equal(HttpStatusCode.Created, createRole.StatusCode);
        await AssertDbAsync(async db =>
        {
            RoleEntity role = await RoleAsync(db, appId, "approver");
            Assert.Equal("Approver", role.Name);
            Assert.Equal("Approves invoices", role.Description);
            Assert.False(role.Privileged);
            Assert.Equal("HIGH", role.RiskLevel);
            Assert.Equal("ACTIVE", role.Status);
        });

        // ── ROLE: update ────────────────────────────────────────────────────────
        using HttpResponseMessage updateRole = await admin.PutAsJsonAsync(
            $"/v1/admin/applications/{appId}/roles/approver",
            new { name = "Senior Approver", description = "Updated description", privileged = false, riskLevel = "LOW" });
        Assert.Equal(HttpStatusCode.OK, updateRole.StatusCode);
        await AssertDbAsync(async db =>
        {
            RoleEntity role = await RoleAsync(db, appId, "approver");
            Assert.Equal("Senior Approver", role.Name);
            Assert.Equal("Updated description", role.Description);
            Assert.Equal("LOW", role.RiskLevel);
        });

        // ── PERMISSION: create ──────────────────────────────────────────────────
        using HttpResponseMessage createPerm = await admin.PostAsJsonAsync(
            $"/v1/admin/applications/{appId}/permissions",
            new { permissionKey = "invoice.approve", resource = "invoice", action = "approve", riskLevel = "LOW" });
        Assert.Equal(HttpStatusCode.Created, createPerm.StatusCode);
        await AssertDbAsync(async db =>
        {
            PermissionEntity perm = await PermissionAsync(db, appId, "invoice.approve");
            Assert.Equal("invoice", perm.Resource);
            Assert.Equal("approve", perm.Action);
        });

        // ── ROLE-PERMISSION MAPPING: create (draft) then publish ────────────────
        using HttpResponseMessage createMap = await admin.PostAsJsonAsync(
            $"/v1/admin/applications/{appId}/role-permissions",
            new { roleKey = "approver", permissionKey = "invoice.approve", publish = false });
        Assert.Equal(HttpStatusCode.Created, createMap.StatusCode);
        MappingResponse? mapping = await createMap.Content.ReadFromJsonAsync<MappingResponse>();
        Assert.NotNull(mapping);
        Assert.Equal("DRAFT", mapping!.State);
        await AssertDbAsync(async db =>
        {
            RolePermissionEntity map = await db.RolePermissions.AsNoTracking().SingleAsync(m => m.Id == mapping.Id);
            Assert.Equal("DRAFT", map.State);
            Assert.Null(map.PublishedAt);
        });

        using HttpResponseMessage publishMap = await admin.PostAsync(
            $"/v1/admin/applications/{appId}/role-permissions/{mapping.Id}/publish", content: null);
        Assert.Equal(HttpStatusCode.OK, publishMap.StatusCode);
        await AssertDbAsync(async db =>
        {
            RolePermissionEntity map = await db.RolePermissions.AsNoTracking().SingleAsync(m => m.Id == mapping.Id);
            Assert.Equal("PUBLISHED", map.State);
            Assert.NotNull(map.PublishedAt);
        });

        // ── POLICY: create ──────────────────────────────────────────────────────
        using HttpResponseMessage createPolicy = await admin.PostAsJsonAsync(
            $"/v1/admin/applications/{appId}/policies",
            new
            {
                policyKey = "approve-positive-amounts",
                permissionKey = "invoice.approve",
                effect = "ALLOW",
                conditions = "{\"conditions\":[{\"attribute\":\"context.amount\",\"operator\":\"gt\",\"value\":\"0\"}]}",
                publish = false,
            });
        Assert.Equal(HttpStatusCode.Created, createPolicy.StatusCode);
        await AssertDbAsync(async db =>
        {
            PolicyEntity policy = await db.Policies.AsNoTracking()
                .SingleAsync(p => p.ApplicationRefId == AppRef(db, appId) && p.PolicyKey == "approve-positive-amounts");
            Assert.Equal("ALLOW", policy.Effect);
        });

        // ── ASSIGNMENT: create ──────────────────────────────────────────────────
        string subject = $"user-{Guid.NewGuid():N}@local.test";
        using HttpResponseMessage createAssign = await admin.PostAsJsonAsync(
            $"/v1/admin/applications/{appId}/assignments",
            new { subjectType = "USER", subjectEmail = subject, roleKey = "approver", validUntil = DateTimeOffset.UtcNow.AddDays(30), reason = "Initial grant" });
        Assert.Equal(HttpStatusCode.Created, createAssign.StatusCode);
        Guid assignmentId = await ReadIdAsync(createAssign);
        await AssertDbAsync(async db =>
        {
            AssignmentEntity assignment = await db.Assignments.AsNoTracking().SingleAsync(a => a.Id == assignmentId);
            Assert.Equal(subject, assignment.SubjectEmail);
            Assert.Equal("ACTIVE", assignment.State);
            Assert.Equal("MANUAL", assignment.Source);
            Assert.Equal("Initial grant", assignment.Reason);
        });

        // ── ASSIGNMENT: update ──────────────────────────────────────────────────
        using HttpResponseMessage updateAssign = await admin.PutAsJsonAsync(
            $"/v1/admin/applications/{appId}/assignments/{assignmentId}",
            new { roleKey = "approver", validUntil = DateTimeOffset.UtcNow.AddDays(60), reason = "Extended grant" });
        Assert.Equal(HttpStatusCode.OK, updateAssign.StatusCode);
        await AssertDbAsync(async db =>
        {
            AssignmentEntity assignment = await db.Assignments.AsNoTracking().SingleAsync(a => a.Id == assignmentId);
            Assert.Equal("Extended grant", assignment.Reason);
        });

        // ── ASSIGNMENT: revoke ──────────────────────────────────────────────────
        using HttpResponseMessage revoke = await admin.PostAsync(
            $"/v1/admin/applications/{appId}/assignments/{assignmentId}/revoke", content: null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        await AssertDbAsync(async db =>
        {
            AssignmentEntity assignment = await db.Assignments.AsNoTracking().SingleAsync(a => a.Id == assignmentId);
            Assert.Equal("REVOKED", assignment.State);
            Assert.NotNull(assignment.RevokedAt);
        });

        // ── MAPPING: delete ─────────────────────────────────────────────────────
        using HttpResponseMessage deleteMap = await admin.DeleteAsync(
            $"/v1/admin/applications/{appId}/role-permissions/{mapping.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteMap.StatusCode);
        await AssertDbAsync(async db => Assert.False(await db.RolePermissions.AnyAsync(m => m.Id == mapping.Id)));

        // ── ROLE (unreferenced): create then delete ─────────────────────────────
        using HttpResponseMessage createTempRole = await admin.PostAsJsonAsync(
            $"/v1/admin/applications/{appId}/roles",
            new { roleKey = "temp", name = "Temp", privileged = false, riskLevel = "LOW" });
        Assert.Equal(HttpStatusCode.Created, createTempRole.StatusCode);
        using HttpResponseMessage deleteRole = await admin.DeleteAsync($"/v1/admin/applications/{appId}/roles/temp");
        Assert.Equal(HttpStatusCode.NoContent, deleteRole.StatusCode);
        await AssertDbAsync(async db =>
            Assert.False(await db.Roles.AnyAsync(r => r.ApplicationRefId == AppRef(db, appId) && r.RoleKey == "temp")));
    }

    [Theory]
    [InlineData("PlatformSuperAdmin", HttpStatusCode.Created, HttpStatusCode.OK)]
    [InlineData("ApplicationAdmin", HttpStatusCode.Created, HttpStatusCode.OK)]
    [InlineData("ReadOnlyViewer", HttpStatusCode.Forbidden, HttpStatusCode.OK)]
    [InlineData("PlatformReadOnlyViewer", HttpStatusCode.Forbidden, HttpStatusCode.OK)]
    [InlineData("None", HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    public async Task WriteAndReadAccess_AreEnforcedPerRole(string roleKind, HttpStatusCode expectedWrite, HttpStatusCode expectedRead)
    {
        HttpClient platform = factory.CreateAdminClient();
        string appId = $"matrix-{Guid.NewGuid():N}";
        await CreateApplicationAsync(platform, appId);

        HttpClient client = ClientForRole(roleKind, appId);

        using HttpResponseMessage write = await client.PostAsJsonAsync(
            $"/v1/admin/applications/{appId}/roles",
            new { roleKey = $"role-{Guid.NewGuid():N}", name = "Role", privileged = false, riskLevel = "LOW" });
        using HttpResponseMessage read = await client.GetAsync($"/v1/admin/applications/{appId}/roles");

        Assert.Equal(expectedWrite, write.StatusCode);
        Assert.Equal(expectedRead, read.StatusCode);
    }

    [Theory]
    [InlineData("PlatformSuperAdmin", HttpStatusCode.OK)]
    [InlineData("ApplicationAdmin", HttpStatusCode.OK)]
    [InlineData("ReadOnlyViewer", HttpStatusCode.OK)]
    [InlineData("PlatformReadOnlyViewer", HttpStatusCode.OK)]
    [InlineData("None", HttpStatusCode.Forbidden)]
    public async Task AuditRead_IsAllowedForEveryRoleWithViewAudit(string roleKind, HttpStatusCode expected)
    {
        HttpClient platform = factory.CreateAdminClient();
        string appId = $"audit-{Guid.NewGuid():N}";
        await CreateApplicationAsync(platform, appId);

        HttpClient client = ClientForRole(roleKind, appId);

        using HttpResponseMessage response = await client.GetAsync($"/v1/admin/audit-events?applicationId={appId}");

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ScopedRoles_CannotActOutsideTheirApplication()
    {
        HttpClient platform = factory.CreateAdminClient();
        string ownAppId = $"own-{Guid.NewGuid():N}";
        string otherAppId = $"other-{Guid.NewGuid():N}";
        await CreateApplicationAsync(platform, ownAppId);
        await CreateApplicationAsync(platform, otherAppId);

        HttpClient admin = factory.CreateClientForApplicationRole(ownAppId, DelegatedAdminRoles.ApplicationAdmin);

        using HttpResponseMessage own = await admin.PostAsJsonAsync(
            $"/v1/admin/applications/{ownAppId}/roles",
            new { roleKey = "scoped", name = "Scoped", privileged = false, riskLevel = "LOW" });
        using HttpResponseMessage cross = await admin.PostAsJsonAsync(
            $"/v1/admin/applications/{otherAppId}/roles",
            new { roleKey = "scoped", name = "Scoped", privileged = false, riskLevel = "LOW" });

        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, cross.StatusCode);
    }

    private HttpClient ClientForRole(string roleKind, string appId) => roleKind switch
    {
        "PlatformSuperAdmin" => factory.CreateAdminClient(),
        "ApplicationAdmin" => factory.CreateClientForApplicationRole(appId, DelegatedAdminRoles.ApplicationAdmin),
        "ReadOnlyViewer" => factory.CreateClientForApplicationRole(appId, DelegatedAdminRoles.ReadOnlyViewer),
        "PlatformReadOnlyViewer" => factory.CreateClientForPlatformRole(DelegatedAdminRoles.PlatformReadOnlyViewer),
        "None" => factory.CreateAuthenticatedClientWithoutRoles(),
        _ => throw new ArgumentOutOfRangeException(nameof(roleKind), roleKind, "Unknown role kind."),
    };

    private async Task AssertDbAsync(Func<AuthorizationDbContext, Task> assertion)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthorizationDbContext db = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        await assertion(db);
    }

    private static Guid AppRef(AuthorizationDbContext db, string appId) =>
        db.Applications.AsNoTracking().Where(a => a.ApplicationId == appId).Select(a => a.Id).Single();

    private static async Task<RoleEntity> RoleAsync(AuthorizationDbContext db, string appId, string roleKey)
    {
        Guid appRefId = AppRef(db, appId);
        return await db.Roles.AsNoTracking().SingleAsync(r => r.ApplicationRefId == appRefId && r.RoleKey == roleKey);
    }

    private static async Task<PermissionEntity> PermissionAsync(AuthorizationDbContext db, string appId, string permissionKey)
    {
        Guid appRefId = AppRef(db, appId);
        return await db.Permissions.AsNoTracking().SingleAsync(p => p.ApplicationRefId == appRefId && p.PermissionKey == permissionKey);
    }

    private static async Task<Guid> ReadIdAsync(HttpResponseMessage response)
    {
        using JsonDocument document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task CreateApplicationAsync(HttpClient client, string applicationId)
    {
        string tenantId = $"tenant-{Guid.NewGuid():N}";
        using HttpResponseMessage tenantResponse = await client.PostAsJsonAsync("/v1/admin/tenants", new { tenantId, name = tenantId });
        tenantResponse.EnsureSuccessStatusCode();

        using HttpResponseMessage applicationResponse = await client.PostAsJsonAsync("/v1/admin/applications", new { applicationId, name = applicationId, tenantId });
        applicationResponse.EnsureSuccessStatusCode();
    }

    private sealed record MappingResponse(Guid Id, string State);
}
