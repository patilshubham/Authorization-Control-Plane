using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Authorization.Api.Contracts;
using Authorization.Api.Controllers;
using Authorization.Api.Errors;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Authorization.Api.Tests.Admin;

public sealed class AdminGovernanceControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public AdminGovernanceControllerTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task ApplicationAndOidcProviderMutations_WriteAudit()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = $"admin-app-{Guid.NewGuid():N}";
        string tenantId = $"tenant-{Guid.NewGuid():N}";

        using HttpResponseMessage tenantResponse = await client.PostAsJsonAsync("/v1/admin/tenants", new
        {
            tenantId,
            name = "Admin Test Tenant",
        });
        Assert.Equal(HttpStatusCode.Created, tenantResponse.StatusCode);

        using HttpResponseMessage applicationResponse = await client.PostAsJsonAsync("/v1/admin/applications", new
        {
            applicationId,
            name = "Admin Test App",
            tenantId,
        });
        using HttpResponseMessage providerResponse = await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/oidc-providers", new
        {
            issuer = "http://issuer.local",
            audience = "authorization-api",
            jwksUri = "http://issuer.local/certs",
            allowedAlgorithms = new[] { "RS256" },
            subjectType = "SERVICE_ACCOUNT",
            subjectClaim = "azp",
        });

        Assert.Equal(HttpStatusCode.Created, applicationResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, providerResponse.StatusCode);

        using JsonDocument providerDoc = JsonDocument.Parse(await providerResponse.Content.ReadAsStringAsync());
        Guid providerId = providerDoc.RootElement.GetProperty("id").GetGuid();

        using HttpResponseMessage updateResponse = await client.PutAsJsonAsync($"/v1/admin/applications/{applicationId}/oidc-providers/{providerId}", new
        {
            issuer = "http://issuer.local",
            audience = "authorization-api",
            jwksUri = "http://issuer.local/certs",
            allowedAlgorithms = new[] { "RS256", "ES256" },
            subjectType = "SERVICE_ACCOUNT",
            subjectClaim = "azp",
            enabled = false,
        });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthorizationDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        Assert.True(await dbContext.Applications.AnyAsync(entity => entity.ApplicationId == applicationId));
        Guid appRefId = await dbContext.Applications.Where(a => a.ApplicationId == applicationId).Select(a => a.Id).SingleAsync();
        Assert.True(await dbContext.OidcProviders.AnyAsync(entity => entity.ApplicationRefId == appRefId));
        Assert.True(await dbContext.OidcProviders.AnyAsync(entity => entity.Id == providerId && !entity.Enabled));
        Assert.True(await dbContext.OidcProviders.AnyAsync(entity => entity.Id == providerId && entity.SubjectType == "SERVICE_ACCOUNT" && entity.SubjectClaim == "azp"));
        Assert.True(await dbContext.AuditEvents.AnyAsync(entity => entity.ApplicationId == applicationId && entity.EventType == "APPLICATION_CREATED"));
        Assert.True(await dbContext.AuditEvents.AnyAsync(entity => entity.ApplicationId == applicationId && entity.EventType == "OIDC_PROVIDER_CREATED"));
        Assert.True(await dbContext.AuditEvents.AnyAsync(entity => entity.ApplicationId == applicationId && entity.EventType == "OIDC_PROVIDER_UPDATED"));
    }

    [Fact]
    public async Task ApplicationLifecycle_DisableActivateArchive_TransitionsStatusAndAudits()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = $"lifecycle-app-{Guid.NewGuid():N}";
        await CreateApplicationAsync(client, applicationId);

        using HttpResponseMessage disableResponse = await client.PostAsync($"/v1/admin/applications/{applicationId}/disable", content: null);
        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);

        using HttpResponseMessage activateResponse = await client.PostAsync($"/v1/admin/applications/{applicationId}/activate", content: null);
        Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);

        using HttpResponseMessage archiveResponse = await client.PostAsync($"/v1/admin/applications/{applicationId}/archive", content: null);
        Assert.Equal(HttpStatusCode.OK, archiveResponse.StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthorizationDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        Assert.True(await dbContext.Applications.AnyAsync(entity => entity.ApplicationId == applicationId && entity.Status == "ARCHIVED"));
        Assert.True(await dbContext.AuditEvents.AnyAsync(entity => entity.ApplicationId == applicationId && entity.EventType == "APPLICATION_DISABLED"));
        Assert.True(await dbContext.AuditEvents.AnyAsync(entity => entity.ApplicationId == applicationId && entity.EventType == "APPLICATION_ACTIVATED"));
        Assert.True(await dbContext.AuditEvents.AnyAsync(entity => entity.ApplicationId == applicationId && entity.EventType == "APPLICATION_ARCHIVED"));
    }

    [Fact]
    public async Task RolePermissionMapping_CreatesAndPublishes()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = $"role-app-{Guid.NewGuid():N}";
        await CreateApplicationAsync(client, applicationId);

        using HttpResponseMessage roleResponse = await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/roles", new
        {
            roleKey = "approver",
            name = "Approver",
            privileged = true,
            riskLevel = "HIGH",
        });
        using HttpResponseMessage permissionResponse = await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/permissions", new
        {
            permissionKey = "invoice.approve",
            resource = "invoice",
            action = "approve",
            riskLevel = "HIGH",
        });
        using HttpResponseMessage mappingResponse = await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/role-permissions", new
        {
            roleKey = "approver",
            permissionKey = "invoice.approve",
            publish = true,
        });

        Assert.Equal(HttpStatusCode.Created, roleResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, permissionResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, mappingResponse.StatusCode);
    }

    [Fact]
    public async Task AdminApi_RejectsUnauthenticatedRequests()
    {
        HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/v1/admin/applications");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AssignmentMutationAndSimulator_ReflectRevocationAndAuditTrail()
    {
        await factory.SeedLocalDataAsync();
        HttpClient client = factory.CreateAdminClient();
        string subjectEmail = $"subject-{Guid.NewGuid():N}@local.test";

        using HttpResponseMessage createResponse = await client.PostAsJsonAsync("/v1/admin/applications/intelligence-authoring/assignments", new
        {
            subjectType = "USER",
            subjectEmail,
            roleKey = "content-author",
            tenantId = "squad-1",
            validUntil = DateTimeOffset.UtcNow.AddDays(1),
            privileged = true,
        });
        createResponse.EnsureSuccessStatusCode();
        JsonDocument createdAssignment = await JsonDocument.ParseAsync(await createResponse.Content.ReadAsStreamAsync());
        Guid assignmentId = createdAssignment.RootElement.GetProperty("id").GetGuid();

        using HttpResponseMessage revokeResponse = await client.PostAsync($"/v1/admin/applications/intelligence-authoring/assignments/{assignmentId}/revoke", null);
        revokeResponse.EnsureSuccessStatusCode();
        using HttpResponseMessage simulatorResponse = await client.PostAsJsonAsync("/v1/admin/simulator/authorize", new
        {
            applicationId = "intelligence-authoring",
            subjectType = "USER",
            subjectEmail,
            resourceType = "article",
            resourceId = "article-1",
            action = "view",
            tenantId = "squad-1",
            context = new Dictionary<string, object?> { ["amount"] = 100 },
        });
        AuthorizeResponse? simulatorBody = await simulatorResponse.Content.ReadFromJsonAsync<AuthorizeResponse>();

        simulatorResponse.EnsureSuccessStatusCode();
        Assert.NotNull(simulatorBody);
        Assert.False(simulatorBody.Allowed);
        Assert.Equal("ASSIGNMENT_REVOKED", simulatorBody.DenyReason);
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthorizationDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        Assert.True(await dbContext.AuditEvents.AnyAsync(entity => entity.EventType == "ASSIGNMENT_REVOKED" && entity.TargetSubjectEmail == subjectEmail));
    }

    [Fact]
    public async Task PolicyValidation_RejectsUnsupportedOperator()
    {
        await factory.SeedLocalDataAsync();
        HttpClient client = factory.CreateAdminClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/admin/applications/finance-app/policies", new
        {
            policyKey = $"invalid-policy-{Guid.NewGuid():N}",
            permissionKey = "invoice.approve",
            effect = "ALLOW",
            conditions = "{\"conditions\":[{\"attribute\":\"context.amount\",\"operator\":\"regex\",\"value\":\".*\"}]}",
        });
        ApiErrorEnvelope? error = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("POLICY_CONDITIONS_INVALID", error.Error.Code);
    }

    [Fact]
    public async Task CreateAssignment_RejectsExpiredValidUntil()
    {
        await factory.SeedLocalDataAsync();
        HttpClient client = factory.CreateAdminClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/admin/applications/finance-app/assignments", new
        {
            subjectType = "USER",
            subjectEmail = $"subject-{Guid.NewGuid():N}@local.test",
            roleKey = "invoice-approver",
            tenantId = "tenant-a",
            validUntil = DateTimeOffset.UtcNow.AddDays(-1),
            privileged = true,
        });
        ApiErrorEnvelope? error = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("VALIDATION_ERROR", error.Error.Code);
    }

    [Fact]
    public async Task CreateAssignment_RejectsPrivilegedRoleWithoutExpiry()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = $"privileged-assign-{Guid.NewGuid():N}";
        await CreateApplicationAsync(client, applicationId);
        await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/roles", new
        {
            roleKey = "ops-admin",
            name = "Ops Admin",
            privileged = true,
            riskLevel = "HIGH",
        });

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/assignments", new
        {
            subjectType = "USER",
            subjectEmail = $"subject-{Guid.NewGuid():N}@local.test",
            roleKey = "ops-admin",
        });
        ApiErrorEnvelope? error = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("PRIVILEGED_ASSIGNMENT_REQUIRES_EXPIRY", error.Error.Code);
    }

    [Fact]
    public async Task CreateAssignment_IgnoresClientSuppliedPrivilegedFlag_DerivesFromRole()
    {
        // Proves the trust-boundary fix: even if a caller sends a stray privileged=false, the
        // server must still enforce the expiry requirement because the role itself is privileged.
        HttpClient client = factory.CreateAdminClient();
        string applicationId = $"privileged-bypass-{Guid.NewGuid():N}";
        await CreateApplicationAsync(client, applicationId);
        await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/roles", new
        {
            roleKey = "ops-admin",
            name = "Ops Admin",
            privileged = true,
            riskLevel = "HIGH",
        });

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/assignments", new
        {
            subjectType = "USER",
            subjectEmail = $"subject-{Guid.NewGuid():N}@local.test",
            roleKey = "ops-admin",
            privileged = false,
        });
        ApiErrorEnvelope? error = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("PRIVILEGED_ASSIGNMENT_REQUIRES_EXPIRY", error.Error.Code);
    }

    [Fact]
    public async Task CreateAssignment_NonPrivilegedRole_DoesNotRequireExpiry()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = $"non-privileged-assign-{Guid.NewGuid():N}";
        await CreateApplicationAsync(client, applicationId);
        await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/roles", new
        {
            roleKey = "ops-viewer",
            name = "Ops Viewer",
            privileged = false,
            riskLevel = "LOW",
        });

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/assignments", new
        {
            subjectType = "USER",
            subjectEmail = $"subject-{Guid.NewGuid():N}@local.test",
            roleKey = "ops-viewer",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task RevokeAssignment_AuditEventContainsOldValue()
    {
        await factory.SeedLocalDataAsync();
        HttpClient client = factory.CreateAdminClient();
        string subjectEmail = $"audit-{Guid.NewGuid():N}@local.test";

        using HttpResponseMessage createResponse = await client.PostAsJsonAsync("/v1/admin/applications/intelligence-authoring/assignments", new
        {
            subjectType = "USER",
            subjectEmail,
            roleKey = "content-author",
            tenantId = "squad-1",
            validUntil = DateTimeOffset.UtcNow.AddDays(1),
            privileged = true,
        });
        createResponse.EnsureSuccessStatusCode();
        JsonDocument createdAssignment = await JsonDocument.ParseAsync(await createResponse.Content.ReadAsStreamAsync());
        Guid assignmentId = createdAssignment.RootElement.GetProperty("id").GetGuid();

        using HttpResponseMessage revokeResponse = await client.PostAsync($"/v1/admin/applications/intelligence-authoring/assignments/{assignmentId}/revoke", null);
        revokeResponse.EnsureSuccessStatusCode();

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthorizationDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        AuditEventEntity? auditEvent = await dbContext.AuditEvents.FirstOrDefaultAsync(entity =>
            entity.EventType == "ASSIGNMENT_REVOKED" && entity.TargetSubjectEmail == subjectEmail);

        Assert.NotNull(auditEvent);
        Assert.NotNull(auditEvent.OldValue);
        Assert.Contains("State", auditEvent.OldValue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishPolicy_AuditEventContainsOldValue()
    {
        await factory.SeedLocalDataAsync();
        HttpClient client = factory.CreateAdminClient();
        string policyKey = $"audit-policy-{Guid.NewGuid():N}";

        using HttpResponseMessage createResponse = await client.PostAsJsonAsync("/v1/admin/applications/intelligence-authoring/policies", new
        {
            policyKey,
            permissionKey = "article.view",
            effect = "ALLOW",
            conditions = "{\"conditions\":[{\"attribute\":\"context.amount\",\"operator\":\"gt\",\"value\":\"0\"}]}",
        });
        createResponse.EnsureSuccessStatusCode();

        using HttpResponseMessage publishResponse = await client.PostAsync($"/v1/admin/applications/intelligence-authoring/policies/{policyKey}/publish", null);
        publishResponse.EnsureSuccessStatusCode();

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthorizationDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        AuditEventEntity? auditEvent = await dbContext.AuditEvents.FirstOrDefaultAsync(entity =>
            entity.EventType == "POLICY_PUBLISHED" && entity.ApplicationId == "intelligence-authoring");

        Assert.NotNull(auditEvent);
        Assert.NotNull(auditEvent.OldValue);
    }

    [Fact]
    public async Task TenantCrud_CreatesTenantAndOwnsApplication()
    {
        HttpClient client = factory.CreateAdminClient();
        string tenantId = $"tenant-{Guid.NewGuid():N}";
        string applicationId = $"tenant-app-{Guid.NewGuid():N}";

        using HttpResponseMessage createTenant = await client.PostAsJsonAsync("/v1/admin/tenants", new
        {
            tenantId,
            name = "Contoso",
            description = "Test tenant",
        });
        Assert.Equal(HttpStatusCode.Created, createTenant.StatusCode);

        using HttpResponseMessage createApp = await client.PostAsJsonAsync("/v1/admin/applications", new
        {
            applicationId,
            name = "Owned App",
            tenantId,
        });
        Assert.Equal(HttpStatusCode.Created, createApp.StatusCode);

        using HttpResponseMessage listTenants = await client.GetAsync("/v1/admin/tenants");
        listTenants.EnsureSuccessStatusCode();

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthorizationDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        Assert.True(await dbContext.Tenants.AnyAsync(entity => entity.TenantId == tenantId));
        Guid tenantRefId = await dbContext.Tenants.Where(t => t.TenantId == tenantId).Select(t => t.Id).SingleAsync();
        Assert.True(await dbContext.Applications.AnyAsync(entity => entity.ApplicationId == applicationId && entity.TenantRefId == tenantRefId));
        Assert.True(await dbContext.AuditEvents.AnyAsync(entity => entity.EventType == "TENANT_CREATED"));
    }

    [Fact]
    public async Task CreateApplication_RejectsUnknownOwningTenant()
    {
        HttpClient client = factory.CreateAdminClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/admin/applications", new
        {
            applicationId = $"orphan-app-{Guid.NewGuid():N}",
            name = "Orphan App",
            tenantId = $"missing-{Guid.NewGuid():N}",
        });
        ApiErrorEnvelope? error = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("TENANT_NOT_FOUND", error.Error.Code);
    }

    [Fact]
    public async Task UpdateApplication_EditsMetadataAndReParentsTenant()
    {
        HttpClient client = factory.CreateAdminClient();
        string firstTenantId = $"tenant-{Guid.NewGuid():N}";
        string secondTenantId = $"tenant-{Guid.NewGuid():N}";
        string applicationId = $"edit-app-{Guid.NewGuid():N}";

        foreach (string tenantId in new[] { firstTenantId, secondTenantId })
        {
            using HttpResponseMessage createTenant = await client.PostAsJsonAsync("/v1/admin/tenants", new { tenantId, name = tenantId });
            Assert.Equal(HttpStatusCode.Created, createTenant.StatusCode);
        }

        using HttpResponseMessage createApp = await client.PostAsJsonAsync("/v1/admin/applications", new { applicationId, name = "Before", tenantId = firstTenantId });
        Assert.Equal(HttpStatusCode.Created, createApp.StatusCode);

        using HttpResponseMessage updateResponse = await client.PutAsJsonAsync($"/v1/admin/applications/{applicationId}", new
        {
            name = "After",
            description = "Edited description",
            tenantId = secondTenantId,
            ownerTeam = "Platform",
            riskLevel = "HIGH",
        });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthorizationDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        ApplicationEntity updated = await dbContext.Applications.SingleAsync(entity => entity.ApplicationId == applicationId);
        Assert.Equal("After", updated.Name);
        Assert.Equal("Edited description", updated.Description);
        string updatedTenantId = await dbContext.Tenants.Where(t => t.Id == updated.TenantRefId).Select(t => t.TenantId).SingleAsync();
        Assert.Equal(secondTenantId, updatedTenantId);
        Assert.Equal("HIGH", updated.RiskLevel);
        Assert.True(await dbContext.AuditEvents.AnyAsync(entity => entity.ApplicationId == applicationId && entity.EventType == "APPLICATION_UPDATED"));
    }

    [Fact]
    public async Task UpdateApplication_RejectsUnknownOwningTenant()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = $"edit-orphan-{Guid.NewGuid():N}";
        await CreateApplicationAsync(client, applicationId);

        using HttpResponseMessage response = await client.PutAsJsonAsync($"/v1/admin/applications/{applicationId}", new
        {
            name = "Edited",
            tenantId = $"missing-{Guid.NewGuid():N}",
        });
        ApiErrorEnvelope? error = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("TENANT_NOT_FOUND", error.Error.Code);
    }

    [Fact]
    public async Task DeleteTenant_BlockedWhileOwningApplications()
    {
        HttpClient client = factory.CreateAdminClient();
        string tenantId = $"tenant-{Guid.NewGuid():N}";
        string applicationId = $"owned-app-{Guid.NewGuid():N}";

        using HttpResponseMessage createTenant = await client.PostAsJsonAsync("/v1/admin/tenants", new { tenantId, name = "Owner" });
        Assert.Equal(HttpStatusCode.Created, createTenant.StatusCode);
        using HttpResponseMessage createApp = await client.PostAsJsonAsync("/v1/admin/applications", new { applicationId, name = "Owned", tenantId });
        Assert.Equal(HttpStatusCode.Created, createApp.StatusCode);

        using HttpResponseMessage deleteResponse = await client.DeleteAsync($"/v1/admin/tenants/{tenantId}");
        ApiErrorEnvelope? error = await deleteResponse.Content.ReadFromJsonAsync<ApiErrorEnvelope>();

        Assert.Equal(HttpStatusCode.Conflict, deleteResponse.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("TENANT_IN_USE", error.Error.Code);
    }

    [Fact]
    public async Task DeleteTenant_SucceedsWhenEmptyAndWritesAudit()
    {
        HttpClient client = factory.CreateAdminClient();
        string tenantId = $"tenant-{Guid.NewGuid():N}";

        using HttpResponseMessage createTenant = await client.PostAsJsonAsync("/v1/admin/tenants", new { tenantId, name = "Disposable" });
        Assert.Equal(HttpStatusCode.Created, createTenant.StatusCode);

        using HttpResponseMessage deleteResponse = await client.DeleteAsync($"/v1/admin/tenants/{tenantId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthorizationDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        Assert.False(await dbContext.Tenants.AnyAsync(entity => entity.TenantId == tenantId));
        Assert.True(await dbContext.AuditEvents.AnyAsync(entity => entity.EventType == "TENANT_DELETED"));
    }

    [Fact]
    public async Task UpdateAssignment_ChangesRoleAndExpiryAndWritesAudit()
    {
        await factory.SeedLocalDataAsync();
        HttpClient client = factory.CreateAdminClient();
        string subjectEmail = $"edit-{Guid.NewGuid():N}@local.test";

        using HttpResponseMessage createResponse = await client.PostAsJsonAsync("/v1/admin/applications/intelligence-authoring/assignments", new
        {
            subjectType = "USER",
            subjectEmail,
            roleKey = "content-viewer",
            validUntil = DateTimeOffset.UtcNow.AddDays(3),
        });
        createResponse.EnsureSuccessStatusCode();
        JsonDocument created = await JsonDocument.ParseAsync(await createResponse.Content.ReadAsStreamAsync());
        Guid assignmentId = created.RootElement.GetProperty("id").GetGuid();

        DateTimeOffset newExpiry = DateTimeOffset.UtcNow.AddDays(30);
        using HttpResponseMessage updateResponse = await client.PutAsJsonAsync($"/v1/admin/applications/intelligence-authoring/assignments/{assignmentId}", new
        {
            roleKey = "content-author",
            validUntil = newExpiry,
            reason = "Promoted to approver",
        });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthorizationDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        AssignmentEntity updated = await dbContext.Assignments.SingleAsync(entity => entity.Id == assignmentId);
        string updatedRoleKey = await dbContext.Roles.Where(r => r.Id == updated.RoleRefId).Select(r => r.RoleKey).SingleAsync();
        Assert.Equal("content-author", updatedRoleKey);
        Assert.Equal("Promoted to approver", updated.Reason);
        AuditEventEntity? auditEvent = await dbContext.AuditEvents.FirstOrDefaultAsync(entity =>
            entity.EventType == "ASSIGNMENT_UPDATED" && entity.TargetSubjectEmail == subjectEmail);
        Assert.NotNull(auditEvent);
        Assert.NotNull(auditEvent.OldValue);
    }

    [Fact]
    public async Task UpdateAndDeleteRole_RoundTripsWithAudits()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = $"role-crud-{Guid.NewGuid():N}";
        await CreateApplicationAsync(client, applicationId);
        await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/roles", new { roleKey = "editor", name = "Editor", privileged = false, riskLevel = "LOW" });

        using HttpResponseMessage updateResponse = await client.PutAsJsonAsync($"/v1/admin/applications/{applicationId}/roles/editor", new
        {
            name = "Senior Editor",
            description = "Can edit and approve",
            privileged = true,
            riskLevel = "HIGH",
        });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        using HttpResponseMessage deleteResponse = await client.DeleteAsync($"/v1/admin/applications/{applicationId}/roles/editor");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthorizationDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        Guid roleCrudAppRefId = await dbContext.Applications.Where(a => a.ApplicationId == applicationId).Select(a => a.Id).SingleAsync();
        Assert.False(await dbContext.Roles.AnyAsync(r => r.ApplicationRefId == roleCrudAppRefId && r.RoleKey == "editor"));
        Assert.True(await dbContext.AuditEvents.AnyAsync(e => e.ApplicationId == applicationId && e.EventType == "ROLE_UPDATED"));
        Assert.True(await dbContext.AuditEvents.AnyAsync(e => e.ApplicationId == applicationId && e.EventType == "ROLE_DELETED"));
    }

    [Fact]
    public async Task RoleLifecycle_DisableArchiveActivate_UpdatesStatusWithAudits()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = $"role-lifecycle-{Guid.NewGuid():N}";
        await CreateApplicationAsync(client, applicationId);
        await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/roles", new { roleKey = "operator", name = "Operator", privileged = false, riskLevel = "LOW" });

        using HttpResponseMessage disableResponse = await client.PostAsync($"/v1/admin/applications/{applicationId}/roles/operator/disable", null);
        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);
        RoleResponse? disabled = await disableResponse.Content.ReadFromJsonAsync<RoleResponse>();
        Assert.NotNull(disabled);
        Assert.Equal("DISABLED", disabled.Status);

        using HttpResponseMessage archiveResponse = await client.PostAsync($"/v1/admin/applications/{applicationId}/roles/operator/archive", null);
        Assert.Equal(HttpStatusCode.OK, archiveResponse.StatusCode);
        RoleResponse? archived = await archiveResponse.Content.ReadFromJsonAsync<RoleResponse>();
        Assert.NotNull(archived);
        Assert.Equal("ARCHIVED", archived.Status);

        using HttpResponseMessage activateResponse = await client.PostAsync($"/v1/admin/applications/{applicationId}/roles/operator/activate", null);
        Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);
        RoleResponse? activated = await activateResponse.Content.ReadFromJsonAsync<RoleResponse>();
        Assert.NotNull(activated);
        Assert.Equal("ACTIVE", activated.Status);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthorizationDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        Assert.True(await dbContext.AuditEvents.AnyAsync(e => e.ApplicationId == applicationId && e.EventType == "ROLE_DISABLED"));
        Assert.True(await dbContext.AuditEvents.AnyAsync(e => e.ApplicationId == applicationId && e.EventType == "ROLE_ARCHIVED"));
        Assert.True(await dbContext.AuditEvents.AnyAsync(e => e.ApplicationId == applicationId && e.EventType == "ROLE_ACTIVATED"));
    }

    [Fact]
    public async Task RoleLifecycle_Disable_ReturnsNotFoundForUnknownRole()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = $"role-lifecycle-404-{Guid.NewGuid():N}";
        await CreateApplicationAsync(client, applicationId);

        using HttpResponseMessage response = await client.PostAsync($"/v1/admin/applications/{applicationId}/roles/ghost/disable", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteRole_RejectsWhenPermissionsStillMapped()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = $"role-guard-{Guid.NewGuid():N}";
        await CreateApplicationAsync(client, applicationId);
        await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/roles", new { roleKey = "approver", name = "Approver", privileged = false, riskLevel = "LOW" });
        await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/permissions", new { permissionKey = "invoice.approve", resource = "invoice", action = "approve", riskLevel = "LOW" });
        await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/role-permissions", new { roleKey = "approver", permissionKey = "invoice.approve", publish = true });

        using HttpResponseMessage deleteResponse = await client.DeleteAsync($"/v1/admin/applications/{applicationId}/roles/approver");
        ApiErrorEnvelope? error = await deleteResponse.Content.ReadFromJsonAsync<ApiErrorEnvelope>();

        Assert.Equal(HttpStatusCode.Conflict, deleteResponse.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("ROLE_IN_USE", error.Error.Code);
    }

    [Fact]
    public async Task UpdateAndDeletePermission_RoundTripsWithAudits()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = $"perm-crud-{Guid.NewGuid():N}";
        await CreateApplicationAsync(client, applicationId);
        await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/permissions", new { permissionKey = "report.view", resource = "report", action = "view", riskLevel = "LOW" });

        using HttpResponseMessage updateResponse = await client.PutAsJsonAsync($"/v1/admin/applications/{applicationId}/permissions/report.view", new
        {
            description = "View financial reports",
            riskLevel = "MEDIUM",
        });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        using HttpResponseMessage deleteResponse = await client.DeleteAsync($"/v1/admin/applications/{applicationId}/permissions/report.view");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthorizationDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        Guid permCrudAppRefId = await dbContext.Applications.Where(a => a.ApplicationId == applicationId).Select(a => a.Id).SingleAsync();
        Assert.False(await dbContext.Permissions.AnyAsync(p => p.ApplicationRefId == permCrudAppRefId && p.PermissionKey == "report.view"));
        Assert.True(await dbContext.AuditEvents.AnyAsync(e => e.ApplicationId == applicationId && e.EventType == "PERMISSION_UPDATED"));
        Assert.True(await dbContext.AuditEvents.AnyAsync(e => e.ApplicationId == applicationId && e.EventType == "PERMISSION_DELETED"));
    }

    [Fact]
    public async Task PermissionLifecycle_DisableArchiveActivate_UpdatesStatusWithAudits()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = $"perm-lifecycle-{Guid.NewGuid():N}";
        await CreateApplicationAsync(client, applicationId);
        await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/permissions", new { permissionKey = "report.view", resource = "report", action = "view", riskLevel = "LOW" });

        using HttpResponseMessage disableResponse = await client.PostAsync($"/v1/admin/applications/{applicationId}/permissions/report.view/disable", null);
        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);
        PermissionResponse? disabled = await disableResponse.Content.ReadFromJsonAsync<PermissionResponse>();
        Assert.NotNull(disabled);
        Assert.Equal("DISABLED", disabled.Status);

        using HttpResponseMessage archiveResponse = await client.PostAsync($"/v1/admin/applications/{applicationId}/permissions/report.view/archive", null);
        Assert.Equal(HttpStatusCode.OK, archiveResponse.StatusCode);
        PermissionResponse? archived = await archiveResponse.Content.ReadFromJsonAsync<PermissionResponse>();
        Assert.NotNull(archived);
        Assert.Equal("ARCHIVED", archived.Status);

        using HttpResponseMessage activateResponse = await client.PostAsync($"/v1/admin/applications/{applicationId}/permissions/report.view/activate", null);
        Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);
        PermissionResponse? activated = await activateResponse.Content.ReadFromJsonAsync<PermissionResponse>();
        Assert.NotNull(activated);
        Assert.Equal("ACTIVE", activated.Status);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthorizationDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        Assert.True(await dbContext.AuditEvents.AnyAsync(e => e.ApplicationId == applicationId && e.EventType == "PERMISSION_DISABLED"));
        Assert.True(await dbContext.AuditEvents.AnyAsync(e => e.ApplicationId == applicationId && e.EventType == "PERMISSION_ARCHIVED"));
        Assert.True(await dbContext.AuditEvents.AnyAsync(e => e.ApplicationId == applicationId && e.EventType == "PERMISSION_ACTIVATED"));
    }

    [Fact]
    public async Task PermissionLifecycle_Disable_ReturnsNotFoundForUnknownPermission()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = $"perm-lifecycle-404-{Guid.NewGuid():N}";
        await CreateApplicationAsync(client, applicationId);

        using HttpResponseMessage response = await client.PostAsync($"/v1/admin/applications/{applicationId}/permissions/ghost.perm/disable", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateReferenceData_PreservesArchivedStatus()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = $"refdata-lifecycle-{Guid.NewGuid():N}";
        await CreateApplicationAsync(client, applicationId);
        await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/reference-data", new { key = "allowed_countries", description = "Allowed", value = "[\"US\",\"CA\"]" });

        // Soft-delete archives the document.
        using HttpResponseMessage deleteResponse = await client.DeleteAsync($"/v1/admin/applications/{applicationId}/reference-data/allowed_countries");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // Editing an archived document must NOT resurrect it.
        using HttpResponseMessage updateResponse = await client.PutAsJsonAsync($"/v1/admin/applications/{applicationId}/reference-data/allowed_countries", new { description = "Edited", value = "[\"US\"]" });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        ReferenceDataResponse? updated = await updateResponse.Content.ReadFromJsonAsync<ReferenceDataResponse>();
        Assert.NotNull(updated);
        Assert.Equal("ARCHIVED", updated.Status);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthorizationDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        Guid appRefId = await dbContext.Applications.Where(a => a.ApplicationId == applicationId).Select(a => a.Id).SingleAsync();
        string status = await dbContext.ReferenceData
            .Where(r => r.ApplicationRefId == appRefId && r.Key == "allowed_countries")
            .Select(r => r.Status)
            .SingleAsync();
        Assert.Equal("ARCHIVED", status);
    }

    [Fact]
    public async Task DeletePermission_RejectsWhenGrantedToRole()
    {
        HttpClient client = factory.CreateAdminClient();
        string applicationId = $"perm-guard-{Guid.NewGuid():N}";
        await CreateApplicationAsync(client, applicationId);
        await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/roles", new { roleKey = "approver", name = "Approver", privileged = false, riskLevel = "LOW" });
        await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/permissions", new { permissionKey = "invoice.approve", resource = "invoice", action = "approve", riskLevel = "LOW" });
        await client.PostAsJsonAsync($"/v1/admin/applications/{applicationId}/role-permissions", new { roleKey = "approver", permissionKey = "invoice.approve", publish = true });

        using HttpResponseMessage deleteResponse = await client.DeleteAsync($"/v1/admin/applications/{applicationId}/permissions/invoice.approve");
        ApiErrorEnvelope? error = await deleteResponse.Content.ReadFromJsonAsync<ApiErrorEnvelope>();

        Assert.Equal(HttpStatusCode.Conflict, deleteResponse.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("PERMISSION_IN_USE", error.Error.Code);
    }

    [Fact]
    public async Task DeletePolicy_RemovesPolicyAndAudits()
    {
        await factory.SeedLocalDataAsync();
        HttpClient client = factory.CreateAdminClient();
        string policyKey = $"delete-policy-{Guid.NewGuid():N}";
        await client.PostAsJsonAsync("/v1/admin/applications/intelligence-authoring/policies", new
        {
            policyKey,
            permissionKey = "article.view",
            effect = "ALLOW",
            conditions = "{\"conditions\":[{\"attribute\":\"context.amount\",\"operator\":\"gt\",\"value\":\"0\"}]}",
        });

        using HttpResponseMessage deleteResponse = await client.DeleteAsync($"/v1/admin/applications/intelligence-authoring/policies/{policyKey}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthorizationDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        Guid appRefId = await dbContext.Applications.Where(a => a.ApplicationId == "intelligence-authoring").Select(a => a.Id).SingleAsync();
        Assert.False(await dbContext.Policies.AnyAsync(p => p.ApplicationRefId == appRefId && p.PolicyKey == policyKey));
        Assert.True(await dbContext.AuditEvents.AnyAsync(e => e.ApplicationId == "intelligence-authoring" && e.EventType == "POLICY_DELETED"));
    }

    private static async Task CreateApplicationAsync(HttpClient client, string applicationId)
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
    }
}
