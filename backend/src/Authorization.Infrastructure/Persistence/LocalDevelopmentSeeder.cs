using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Authorization.Infrastructure.Persistence;

public sealed class LocalDevelopmentSeeder
{
    private const string SeedActor = "local-seed";
    private readonly AuthorizationDbContext dbContext;
    private readonly string runtimeIssuer;
    private readonly string runtimeAudience;
    private readonly string runtimeJwksUri;
    private readonly Dictionary<string, Guid> tenantIds = new();
    private readonly Dictionary<string, Guid> applicationIds = new();
    private readonly Dictionary<(Guid ApplicationRefId, string RoleKey), Guid> roleIds = new();
    private readonly Dictionary<(Guid ApplicationRefId, string PermissionKey), Guid> permissionIds = new();

    public LocalDevelopmentSeeder(AuthorizationDbContext dbContext, IConfiguration configuration)
    {
        this.dbContext = dbContext;

        // Seed the runtime OIDC provider from the same settings the API uses to validate tokens so the
        // two can never drift. The issuer must be the public authority that appears in minted tokens
        // (Keycloak's KC_HOSTNAME), while JWKS is fetched over the back-channel address reachable from
        // the API container.
        runtimeIssuer = configuration["Oidc:Authority"]
            ?? "http://localhost:8081/realms/authorization-local";
        runtimeAudience = configuration["Oidc:Audience"] ?? "authorization-api";

        string metadataAddress = configuration["Oidc:MetadataAddress"]
            ?? "http://keycloak:8080/realms/authorization-local/.well-known/openid-configuration";
        runtimeJwksUri = metadataAddress.Replace(
            "/.well-known/openid-configuration",
            "/protocol/openid-connect/certs",
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await SeedTenantsAsync(cancellationToken);
        await SeedApplicationsAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await SeedOidcProvidersAsync(cancellationToken);
        await SeedRolesAsync(cancellationToken);
        await SeedPermissionsAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await SeedRolePermissionsAsync(cancellationToken);
        await SeedAssignmentsAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await SeedAssignmentAttributesAsync(cancellationToken);
        await SeedReferenceDataAsync(cancellationToken);
        await SeedPoliciesAsync(cancellationToken);
        await SeedSodRulesAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await SeedReviewCampaignsAsync(cancellationToken);
        await SeedDecisionsAsync(cancellationToken);
        await SeedAuditEventsAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Guid> ResolveTenantIdAsync(string tenantId, CancellationToken cancellationToken)
    {
        if (!tenantIds.TryGetValue(tenantId, out Guid id))
        {
            id = await dbContext.Tenants.Where(entity => entity.TenantId == tenantId).Select(entity => entity.Id).FirstAsync(cancellationToken);
            tenantIds[tenantId] = id;
        }

        return id;
    }

    private async Task<Guid> ResolveApplicationIdAsync(string applicationId, CancellationToken cancellationToken)
    {
        if (!applicationIds.TryGetValue(applicationId, out Guid id))
        {
            id = await dbContext.Applications.Where(entity => entity.ApplicationId == applicationId).Select(entity => entity.Id).FirstAsync(cancellationToken);
            applicationIds[applicationId] = id;
        }

        return id;
    }

    private async Task<Guid> ResolveRoleIdAsync(Guid applicationRefId, string roleKey, CancellationToken cancellationToken)
    {
        if (!roleIds.TryGetValue((applicationRefId, roleKey), out Guid id))
        {
            id = await dbContext.Roles.Where(entity => entity.ApplicationRefId == applicationRefId && entity.RoleKey == roleKey).Select(entity => entity.Id).FirstAsync(cancellationToken);
            roleIds[(applicationRefId, roleKey)] = id;
        }

        return id;
    }

    private async Task<Guid> ResolvePermissionIdAsync(Guid applicationRefId, string permissionKey, CancellationToken cancellationToken)
    {
        if (!permissionIds.TryGetValue((applicationRefId, permissionKey), out Guid id))
        {
            id = await dbContext.Permissions.Where(entity => entity.ApplicationRefId == applicationRefId && entity.PermissionKey == permissionKey).Select(entity => entity.Id).FirstAsync(cancellationToken);
            permissionIds[(applicationRefId, permissionKey)] = id;
        }

        return id;
    }

    private async Task SeedTenantsAsync(CancellationToken cancellationToken)
    {
        await AddTenantAsync("squad-1", "Squad 1", "Editorial authoring domain (ICIS Canvas authoring).", cancellationToken);
        await AddTenantAsync("squad-2", "Squad 2", "LNG Edge data-subscription domain.", cancellationToken);
    }

    private async Task AddTenantAsync(string tenantId, string name, string description, CancellationToken cancellationToken)
    {
        if (await dbContext.Tenants.AnyAsync(entity => entity.TenantId == tenantId, cancellationToken))
        {
            return;
        }

        dbContext.Tenants.Add(new TenantEntity
        {
            TenantId = tenantId,
            Name = name,
            Description = description,
            Status = "ACTIVE",
            CreatedBy = SeedActor,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedApplicationsAsync(CancellationToken cancellationToken)
    {
        await AddApplicationAsync("intelligence-authoring", "Intelligence Authoring", tenantId: "squad-1", cancellationToken, riskLevel: "MEDIUM");
        await AddApplicationAsync("pricing-management", "Pricing Management", tenantId: "squad-1", cancellationToken, riskLevel: "HIGH");
        await AddApplicationAsync("market-reference", "Market Reference", tenantId: "squad-1", cancellationToken, riskLevel: "LOW");
        await AddApplicationAsync("lng-edge", "LNG Edge", tenantId: "squad-2", cancellationToken, riskLevel: "HIGH");
    }

    private async Task AddApplicationAsync(string applicationId, string name, string tenantId, CancellationToken cancellationToken, string? riskLevel = null)
    {
        if (await dbContext.Applications.AnyAsync(entity => entity.ApplicationId == applicationId, cancellationToken))
        {
            return;
        }

        Guid tenantRefId = await ResolveTenantIdAsync(tenantId, cancellationToken);
        dbContext.Applications.Add(new ApplicationEntity
        {
            ApplicationId = applicationId,
            Name = name,
            Description = $"Local seeded {name}.",
            TenantRefId = tenantRefId,
            OwnerTeam = "Authorization Platform Engineering",
            BusinessOwner = $"owner-{applicationId}@local.test",
            TechnicalOwner = $"tech-{applicationId}@local.test",
            RiskLevel = riskLevel ?? "MEDIUM",
            Status = "ACTIVE",
            SourceOfTruthMode = "PLATFORM_OWNED",
            CreatedBy = SeedActor,
        });
    }

    private async Task SeedOidcProvidersAsync(CancellationToken cancellationToken)
    {
        // Machine-to-machine (client-credentials) providers: the subject is the calling service,
        // taken from the client-id claim (azp).
        await AddOidcProviderAsync("intelligence-authoring", "intelligence-authoring-runtime-client", "SERVICE_ACCOUNT", "azp", cancellationToken);
        await AddOidcProviderAsync("pricing-management", "pricing-management-runtime-client", "SERVICE_ACCOUNT", "azp", cancellationToken);
        await AddOidcProviderAsync("market-reference", "market-reference-runtime-client", "SERVICE_ACCOUNT", "azp", cancellationToken);
        await AddOidcProviderAsync("lng-edge", "lng-edge-runtime-client", "SERVICE_ACCOUNT", "azp", cancellationToken);

        // User (interactive) provider for pricing-management: the subject is the end user, taken from
        // the user-identity claim (preferred_username).
        await AddOidcProviderAsync("pricing-management", "pricing-management-user-client", "USER", "preferred_username", cancellationToken);
    }

    private async Task AddOidcProviderAsync(string applicationId, string clientId, string subjectType, string subjectClaim, CancellationToken cancellationToken)
    {
        Guid applicationRefId = await ResolveApplicationIdAsync(applicationId, cancellationToken);
        string requiredClaims = $"{{\"azp\":\"{clientId}\"}}";
        if (await dbContext.OidcProviders.AnyAsync(entity => entity.ApplicationRefId == applicationRefId && entity.RequiredClaims == requiredClaims, cancellationToken))
        {
            return;
        }

        dbContext.OidcProviders.Add(new OidcProviderEntity
        {
            ApplicationRefId = applicationRefId,
            ProviderType = "OIDC",
            Issuer = runtimeIssuer,
            Audience = runtimeAudience,
            JwksUri = runtimeJwksUri,
            AllowedAlgorithms = ["RS256"],
            RequiredClaims = requiredClaims,
            ClaimMappings = "{}",
            SubjectType = subjectType,
            SubjectClaim = subjectClaim,
            Enabled = true,
            CreatedBy = SeedActor,
        });
    }

    private async Task SeedRolesAsync(CancellationToken cancellationToken)
    {
        await AddRoleAsync("intelligence-authoring", "content-viewer", "Content Viewer", privileged: false, cancellationToken);
        await AddRoleAsync("intelligence-authoring", "content-author", "Content Author", privileged: false, cancellationToken);
        await AddRoleAsync("intelligence-authoring", "content-editor", "Content Editor", privileged: false, cancellationToken);
        await AddRoleAsync("intelligence-authoring", "content-publisher", "Content Publisher", privileged: false, cancellationToken);
        await AddRoleAsync("intelligence-authoring", "intelligence-admin", "Intelligence Admin", privileged: true, cancellationToken);
        await AddRoleAsync("pricing-management", "pricing-viewer", "Pricing Viewer", privileged: false, cancellationToken);
        await AddRoleAsync("pricing-management", "pricing-analyst", "Pricing Analyst", privileged: false, cancellationToken);
        await AddRoleAsync("pricing-management", "pricing-editor", "Pricing Editor", privileged: false, cancellationToken);
        await AddRoleAsync("pricing-management", "pricing-lead", "Pricing Lead", privileged: true, cancellationToken);
        await AddRoleAsync("pricing-management", "pricing-admin", "Pricing Admin", privileged: true, cancellationToken);
        await AddRoleAsync("market-reference", "reference-viewer", "Reference Viewer", privileged: false, cancellationToken);
        await AddRoleAsync("market-reference", "reference-editor", "Reference Editor", privileged: false, cancellationToken);
        await AddRoleAsync("market-reference", "reference-admin", "Reference Admin", privileged: true, cancellationToken);
        await AddRoleAsync("lng-edge", "lng-viewer", "LNG Viewer", privileged: false, cancellationToken);
        await AddRoleAsync("lng-edge", "lng-trader", "LNG Trader", privileged: false, cancellationToken);
        await AddRoleAsync("lng-edge", "lng-forecaster", "LNG Forecaster", privileged: false, cancellationToken);
        await AddRoleAsync("lng-edge", "lng-analyst", "LNG Analyst", privileged: false, cancellationToken);
        await AddRoleAsync("lng-edge", "lng-admin", "LNG Admin", privileged: true, cancellationToken);
    }

    private async Task AddRoleAsync(string applicationId, string roleKey, string name, bool privileged, CancellationToken cancellationToken)
    {
        Guid applicationRefId = await ResolveApplicationIdAsync(applicationId, cancellationToken);
        if (await dbContext.Roles.AnyAsync(entity => entity.ApplicationRefId == applicationRefId && entity.RoleKey == roleKey, cancellationToken))
        {
            return;
        }

        dbContext.Roles.Add(new RoleEntity
        {
            ApplicationRefId = applicationRefId,
            RoleKey = roleKey,
            Name = name,
            Privileged = privileged,
            RiskLevel = privileged ? "HIGH" : "MEDIUM",
            Status = "ACTIVE",
            CreatedBy = SeedActor,
        });
    }

    private async Task SeedPermissionsAsync(CancellationToken cancellationToken)
    {
        await AddPermissionAsync("intelligence-authoring", "article.view", "article", "view", cancellationToken);
        await AddPermissionAsync("intelligence-authoring", "article.create", "article", "create", cancellationToken);
        await AddPermissionAsync("intelligence-authoring", "article.edit", "article", "edit", cancellationToken);
        await AddPermissionAsync("intelligence-authoring", "article.submit", "article", "submit", cancellationToken);
        await AddPermissionAsync("intelligence-authoring", "article.review", "article", "review", cancellationToken);
        await AddPermissionAsync("intelligence-authoring", "article.publish", "article", "publish", cancellationToken);
        await AddPermissionAsync("intelligence-authoring", "article.unpublish", "article", "unpublish", cancellationToken);
        await AddPermissionAsync("intelligence-authoring", "article.delete", "article", "delete", cancellationToken);
        await AddPermissionAsync("pricing-management", "price.view", "price", "view", cancellationToken);
        await AddPermissionAsync("pricing-management", "price.edit", "price", "edit", cancellationToken);
        await AddPermissionAsync("pricing-management", "price.submit", "price", "submit", cancellationToken);
        await AddPermissionAsync("pricing-management", "price.review", "price", "review", cancellationToken);
        await AddPermissionAsync("pricing-management", "price.publish", "price", "publish", cancellationToken);
        await AddPermissionAsync("pricing-management", "price.correct", "price", "correct", cancellationToken);
        await AddPermissionAsync("market-reference", "facility.view", "facility", "view", cancellationToken);
        await AddPermissionAsync("market-reference", "facility.edit", "facility", "edit", cancellationToken);
        await AddPermissionAsync("market-reference", "platform-message.view", "platform-message", "view", cancellationToken);
        await AddPermissionAsync("market-reference", "platform-message.publish", "platform-message", "publish", cancellationToken);
        await AddPermissionAsync("lng-edge", "shipping.view", "shipping", "view", cancellationToken);
        await AddPermissionAsync("lng-edge", "shipping.export", "shipping", "export", cancellationToken);
        await AddPermissionAsync("lng-edge", "commercial.view", "commercial", "view", cancellationToken);
        await AddPermissionAsync("lng-edge", "commercial.export", "commercial", "export", cancellationToken);
        await AddPermissionAsync("lng-edge", "infrastructure.view", "infrastructure", "view", cancellationToken);
        await AddPermissionAsync("lng-edge", "flows.view", "flows", "view", cancellationToken);
        await AddPermissionAsync("lng-edge", "flows.export", "flows", "export", cancellationToken);
        await AddPermissionAsync("lng-edge", "forecasts.view", "forecasts", "view", cancellationToken);
        await AddPermissionAsync("lng-edge", "forecasts.export", "forecasts", "export", cancellationToken);
        await AddPermissionAsync("lng-edge", "news.view", "news", "view", cancellationToken);
        await AddPermissionAsync("lng-edge", "watchlist.manage", "watchlist", "manage", cancellationToken);
        await AddPermissionAsync("lng-edge", "alert.manage", "alert", "manage", cancellationToken);
        await AddPermissionAsync("lng-edge", "data.download", "data", "download", cancellationToken);
    }

    private async Task AddPermissionAsync(string applicationId, string permissionKey, string resource, string action, CancellationToken cancellationToken)
    {
        Guid applicationRefId = await ResolveApplicationIdAsync(applicationId, cancellationToken);
        if (await dbContext.Permissions.AnyAsync(entity => entity.ApplicationRefId == applicationRefId && entity.PermissionKey == permissionKey, cancellationToken))
        {
            return;
        }

        dbContext.Permissions.Add(new PermissionEntity
        {
            ApplicationRefId = applicationRefId,
            PermissionKey = permissionKey,
            Resource = resource,
            Action = action,
            Description = $"Allows {action} on {resource}.",
            RiskLevel = action is "approve" or "release" or "update" ? "HIGH" : "MEDIUM",
            Status = "ACTIVE",
            CreatedBy = SeedActor,
        });
    }

    private async Task SeedRolePermissionsAsync(CancellationToken cancellationToken)
    {
        await AddRolePermissionAsync("intelligence-authoring", "content-viewer", "article.view", cancellationToken);
        await AddRolePermissionAsync("intelligence-authoring", "content-author", "article.view", cancellationToken);
        await AddRolePermissionAsync("intelligence-authoring", "content-author", "article.create", cancellationToken);
        await AddRolePermissionAsync("intelligence-authoring", "content-author", "article.edit", cancellationToken);
        await AddRolePermissionAsync("intelligence-authoring", "content-author", "article.submit", cancellationToken);
        await AddRolePermissionAsync("intelligence-authoring", "content-editor", "article.view", cancellationToken);
        await AddRolePermissionAsync("intelligence-authoring", "content-editor", "article.edit", cancellationToken);
        await AddRolePermissionAsync("intelligence-authoring", "content-editor", "article.review", cancellationToken);
        await AddRolePermissionAsync("intelligence-authoring", "content-publisher", "article.view", cancellationToken);
        await AddRolePermissionAsync("intelligence-authoring", "content-publisher", "article.publish", cancellationToken);
        await AddRolePermissionAsync("intelligence-authoring", "content-publisher", "article.unpublish", cancellationToken);
        await AddRolePermissionAsync("intelligence-authoring", "intelligence-admin", "article.view", cancellationToken);
        await AddRolePermissionAsync("intelligence-authoring", "intelligence-admin", "article.create", cancellationToken);
        await AddRolePermissionAsync("intelligence-authoring", "intelligence-admin", "article.edit", cancellationToken);
        await AddRolePermissionAsync("intelligence-authoring", "intelligence-admin", "article.submit", cancellationToken);
        await AddRolePermissionAsync("intelligence-authoring", "intelligence-admin", "article.review", cancellationToken);
        await AddRolePermissionAsync("intelligence-authoring", "intelligence-admin", "article.publish", cancellationToken);
        await AddRolePermissionAsync("intelligence-authoring", "intelligence-admin", "article.unpublish", cancellationToken);
        await AddRolePermissionAsync("intelligence-authoring", "intelligence-admin", "article.delete", cancellationToken);
        await AddRolePermissionAsync("pricing-management", "pricing-viewer", "price.view", cancellationToken);
        await AddRolePermissionAsync("pricing-management", "pricing-analyst", "price.view", cancellationToken);
        await AddRolePermissionAsync("pricing-management", "pricing-analyst", "price.edit", cancellationToken);
        await AddRolePermissionAsync("pricing-management", "pricing-analyst", "price.submit", cancellationToken);
        await AddRolePermissionAsync("pricing-management", "pricing-editor", "price.view", cancellationToken);
        await AddRolePermissionAsync("pricing-management", "pricing-editor", "price.review", cancellationToken);
        await AddRolePermissionAsync("pricing-management", "pricing-lead", "price.view", cancellationToken);
        await AddRolePermissionAsync("pricing-management", "pricing-lead", "price.publish", cancellationToken);
        await AddRolePermissionAsync("pricing-management", "pricing-lead", "price.correct", cancellationToken);
        await AddRolePermissionAsync("pricing-management", "pricing-admin", "price.view", cancellationToken);
        await AddRolePermissionAsync("pricing-management", "pricing-admin", "price.edit", cancellationToken);
        await AddRolePermissionAsync("pricing-management", "pricing-admin", "price.submit", cancellationToken);
        await AddRolePermissionAsync("pricing-management", "pricing-admin", "price.review", cancellationToken);
        await AddRolePermissionAsync("pricing-management", "pricing-admin", "price.publish", cancellationToken);
        await AddRolePermissionAsync("pricing-management", "pricing-admin", "price.correct", cancellationToken);
        // A DRAFT mapping pending publish — demonstrates the role→permission draft lifecycle.
        await AddRolePermissionAsync("pricing-management", "pricing-editor", "price.submit", cancellationToken, state: "DRAFT");
        await AddRolePermissionAsync("market-reference", "reference-viewer", "facility.view", cancellationToken);
        await AddRolePermissionAsync("market-reference", "reference-viewer", "platform-message.view", cancellationToken);
        await AddRolePermissionAsync("market-reference", "reference-editor", "facility.view", cancellationToken);
        await AddRolePermissionAsync("market-reference", "reference-editor", "facility.edit", cancellationToken);
        await AddRolePermissionAsync("market-reference", "reference-editor", "platform-message.view", cancellationToken);
        await AddRolePermissionAsync("market-reference", "reference-admin", "facility.view", cancellationToken);
        await AddRolePermissionAsync("market-reference", "reference-admin", "facility.edit", cancellationToken);
        await AddRolePermissionAsync("market-reference", "reference-admin", "platform-message.view", cancellationToken);
        await AddRolePermissionAsync("market-reference", "reference-admin", "platform-message.publish", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-viewer", "shipping.view", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-viewer", "commercial.view", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-viewer", "news.view", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-trader", "shipping.view", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-trader", "shipping.export", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-trader", "commercial.view", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-trader", "commercial.export", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-trader", "flows.view", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-trader", "watchlist.manage", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-trader", "alert.manage", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-forecaster", "forecasts.view", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-forecaster", "forecasts.export", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-analyst", "shipping.view", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-analyst", "shipping.export", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-analyst", "commercial.view", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-analyst", "commercial.export", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-analyst", "infrastructure.view", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-analyst", "flows.view", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-analyst", "flows.export", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-analyst", "forecasts.view", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-analyst", "forecasts.export", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-analyst", "news.view", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-analyst", "watchlist.manage", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-analyst", "alert.manage", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-analyst", "data.download", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-admin", "shipping.view", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-admin", "shipping.export", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-admin", "commercial.view", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-admin", "commercial.export", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-admin", "infrastructure.view", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-admin", "flows.view", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-admin", "flows.export", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-admin", "forecasts.view", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-admin", "forecasts.export", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-admin", "news.view", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-admin", "watchlist.manage", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-admin", "alert.manage", cancellationToken);
        await AddRolePermissionAsync("lng-edge", "lng-admin", "data.download", cancellationToken);
    }

    private async Task AddRolePermissionAsync(string applicationId, string roleKey, string permissionKey, CancellationToken cancellationToken, string state = "PUBLISHED")
    {
        Guid applicationRefId = await ResolveApplicationIdAsync(applicationId, cancellationToken);
        Guid roleRefId = await ResolveRoleIdAsync(applicationRefId, roleKey, cancellationToken);
        Guid permissionRefId = await ResolvePermissionIdAsync(applicationRefId, permissionKey, cancellationToken);
        if (await dbContext.RolePermissions.AnyAsync(entity => entity.RoleRefId == roleRefId && entity.PermissionRefId == permissionRefId, cancellationToken))
        {
            return;
        }

        dbContext.RolePermissions.Add(new RolePermissionEntity
        {
            ApplicationRefId = applicationRefId,
            RoleRefId = roleRefId,
            PermissionRefId = permissionRefId,
            State = state,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            PublishedAt = state == "PUBLISHED" ? DateTimeOffset.UtcNow.AddDays(-1) : null,
            CreatedBy = SeedActor,
        });
    }

    private async Task SeedAssignmentsAsync(CancellationToken cancellationToken)
    {
        // Privileged roles must be time-boxed (never a standing/permanent grant) — declared up front
        // so it's in scope for every privileged assignment below, including intelligence-admin.
        DateTimeOffset boxedExpiry = DateTimeOffset.UtcNow.AddDays(30);

        await AddAssignmentAsync("intelligence-authoring", "user1.author@icis.com", "content-author", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken);
        await AddAssignmentAsync("intelligence-authoring", "user2.editor@icis.com", "content-editor", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken);
        await AddAssignmentAsync("intelligence-authoring", "user3.publisher@icis.com", "content-publisher", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken);
        await AddAssignmentAsync("intelligence-authoring", "user4.admin@icis.com", "intelligence-admin", validUntil: boxedExpiry, state: "ACTIVE", revokedAt: null, cancellationToken);
        // ── Pricing Management: full-feature demo across 20 distinct users ─────────────────
        // Exercises every assignment dimension: ACTIVE / EXPIRED / REVOKED state; MANUAL / IMPORT /
        // EMERGENCY (break-glass) source; standing vs time-boxed grants; privileged roles; aged
        // grants for access certification; a service account; and a user-level Separation-of-Duties
        // violation (submit via analyst + publish via lead held together).
        DateTimeOffset agedGrant = DateTimeOffset.UtcNow.AddDays(-120);
        DateTimeOffset emergencyExpiry = DateTimeOffset.UtcNow.AddHours(4);
        DateTimeOffset pastExpiry = DateTimeOffset.UtcNow.AddDays(-3);

        // Standing, manually-granted business users.
        await AddAssignmentAsync("pricing-management", "user5.analyst@icis.com", "pricing-analyst", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken);
        await AddAssignmentAsync("pricing-management", "user6.editor@icis.com", "pricing-editor", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken);
        await AddAssignmentAsync("pricing-management", "user16.viewer@icis.com", "pricing-viewer", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken);
        await AddAssignmentAsync("pricing-management", "user17.viewer@icis.com", "pricing-viewer", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken);
        await AddAssignmentAsync("pricing-management", "user23.analyst@icis.com", "pricing-analyst", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken);
        await AddAssignmentAsync("pricing-management", "user24.editor@icis.com", "pricing-editor", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken);
        await AddAssignmentAsync("pricing-management", "user25.viewer@icis.com", "pricing-viewer", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken);

        // Time-boxed privileged grants (leads expire and must be renewed).
        await AddAssignmentAsync("pricing-management", "user7.lead@icis.com", "pricing-lead", validUntil: boxedExpiry, state: "ACTIVE", revokedAt: null, cancellationToken);
        await AddAssignmentAsync("pricing-management", "user28.lead@icis.com", "pricing-lead", validUntil: boxedExpiry, state: "ACTIVE", revokedAt: null, cancellationToken);

        // Bulk-imported users (Source = IMPORT).
        await AddAssignmentAsync("pricing-management", "user14.analyst@icis.com", "pricing-analyst", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken, source: "IMPORT");
        await AddAssignmentAsync("pricing-management", "user15.editor@icis.com", "pricing-editor", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken, source: "IMPORT");

        // Break-glass emergency grants (Source = EMERGENCY, short expiry, mandatory reason).
        await AddAssignmentAsync("pricing-management", "user20.lead@icis.com", "pricing-lead", validUntil: emergencyExpiry, state: "ACTIVE", revokedAt: null, cancellationToken, source: "EMERGENCY", reason: "INC-4821 emergency price correction.");
        await AddAssignmentAsync("pricing-management", "user21.admin@icis.com", "pricing-admin", validUntil: emergencyExpiry, state: "ACTIVE", revokedAt: null, cancellationToken, source: "EMERGENCY", reason: "INC-4821 incident-bridge admin access.");

        // Expired grants (past their validity window).
        await AddAssignmentAsync("pricing-management", "user18.analyst@icis.com", "pricing-analyst", validUntil: pastExpiry, state: "EXPIRED", revokedAt: null, cancellationToken);
        await AddAssignmentAsync("pricing-management", "user19.editor@icis.com", "pricing-editor", validUntil: pastExpiry, state: "EXPIRED", revokedAt: null, cancellationToken);

        // Revoked grant (retained as history).
        await AddAssignmentAsync("pricing-management", "user8.admin@icis.com", "pricing-admin", validUntil: null, state: "REVOKED", revokedAt: DateTimeOffset.UtcNow.AddDays(-1), cancellationToken);

        // User-level Separation-of-Duties violation: submit (analyst) + publish (lead) held together.
        await AddAssignmentAsync("pricing-management", "user22.sod@icis.com", "pricing-analyst", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken);
        await AddAssignmentAsync("pricing-management", "user22.sod@icis.com", "pricing-lead", validUntil: boxedExpiry, state: "ACTIVE", revokedAt: null, cancellationToken);

        // Machine-to-machine subject: the pricing-management runtime service account holds a role.
        // pricing-lead grants price.publish, gated by the allow-publish-ready policy. Privileged roles
        // must be time-boxed even for service accounts — no standing-access exception.
        await AddAssignmentAsync("pricing-management", "pricing-management-runtime-client", "pricing-lead", validUntil: boxedExpiry, state: "ACTIVE", revokedAt: null, cancellationToken, subjectType: "SERVICE_ACCOUNT");

        // Access-certification (F4) demo subjects — aged grants exercising each recommendation:
        //   user13 pricing-analyst : recently exercised (seeded decision)      -> KEEP
        //   user13 pricing-editor  : never exercised, non-privileged           -> REVOKE
        //   user13 pricing-admin   : never exercised, privileged, no peers     -> REVIEW
        //   user26 pricing-analyst : recently exercised                        -> KEEP
        //   user27 pricing-editor  : never exercised                          -> REVOKE
        await AddAssignmentAsync("pricing-management", "user13.reviewer@icis.com", "pricing-analyst", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken, validFrom: agedGrant);
        await AddAssignmentAsync("pricing-management", "user13.reviewer@icis.com", "pricing-editor", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken, validFrom: agedGrant);
        await AddAssignmentAsync("pricing-management", "user13.reviewer@icis.com", "pricing-admin", validUntil: boxedExpiry, state: "ACTIVE", revokedAt: null, cancellationToken, validFrom: agedGrant);
        await AddAssignmentAsync("pricing-management", "user26.analyst@icis.com", "pricing-analyst", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken, validFrom: agedGrant);
        await AddAssignmentAsync("pricing-management", "user27.editor@icis.com", "pricing-editor", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken, validFrom: agedGrant);
        await AddAssignmentAsync("lng-edge", "user9.viewer@icis.com", "lng-viewer", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken);
        await AddAssignmentAsync("lng-edge", "user10.trader@icis.com", "lng-trader", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken);
        await AddAssignmentAsync("lng-edge", "user11.forecaster@icis.com", "lng-forecaster", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken);
        await AddAssignmentAsync("lng-edge", "user12.analyst@icis.com", "lng-analyst", validUntil: null, state: "ACTIVE", revokedAt: null, cancellationToken);
    }

    private async Task AddAssignmentAsync(string applicationId, string subjectEmail, string roleKey, DateTimeOffset? validUntil, string state, DateTimeOffset? revokedAt, CancellationToken cancellationToken, string subjectType = "USER", DateTimeOffset? validFrom = null, string source = "MANUAL", string? reason = null)
    {
        Guid applicationRefId = await ResolveApplicationIdAsync(applicationId, cancellationToken);
        Guid roleRefId = await ResolveRoleIdAsync(applicationRefId, roleKey, cancellationToken);
        if (await dbContext.Assignments.AnyAsync(entity => entity.ApplicationRefId == applicationRefId && entity.SubjectEmail == subjectEmail && entity.RoleRefId == roleRefId, cancellationToken))
        {
            return;
        }

        dbContext.Assignments.Add(new AssignmentEntity
        {
            ApplicationRefId = applicationRefId,
            SubjectType = subjectType,
            SubjectEmail = subjectEmail,
            RoleRefId = roleRefId,
            ValidFrom = validFrom ?? DateTimeOffset.UtcNow.AddDays(-1),
            ValidUntil = validUntil,
            RevokedAt = revokedAt,
            Source = source,
            State = state,
            Reason = reason ?? "Local development seed assignment.",
            CreatedBy = SeedActor,
        });
    }

    private async Task SeedDecisionsAsync(CancellationToken cancellationToken)
    {
        // Recorded usage backing the access-certification demo: the recertification subject actively
        // exercised pricing-analyst recently, so that grant is recommended KEEP while their unused
        // pricing-editor / pricing-admin grants surface as REVOKE / REVIEW.
        await AddDecisionAsync(
            decisionId: "seed-recert-user13-analyst",
            applicationId: "pricing-management",
            subjectEmail: "user13.reviewer@icis.com",
            action: "price.view",
            matchedRoles: ["pricing-analyst"],
            timestamp: DateTimeOffset.UtcNow.AddDays(-5),
            cancellationToken);

        // Recently-exercised analyst grant (certification KEEP) plus a spread of allowed decisions
        // that populate the audit/activity feed and the natural-language access-search surface.
        await AddDecisionAsync("seed-recert-user26-analyst", "pricing-management", "user26.analyst@icis.com", "price.view", ["pricing-analyst"], DateTimeOffset.UtcNow.AddDays(-3), cancellationToken);
        await AddDecisionAsync("seed-pricing-lead-publish", "pricing-management", "user7.lead@icis.com", "price.publish", ["pricing-lead"], DateTimeOffset.UtcNow.AddDays(-2), cancellationToken);
        await AddDecisionAsync("seed-pricing-editor-review", "pricing-management", "user6.editor@icis.com", "price.review", ["pricing-editor"], DateTimeOffset.UtcNow.AddDays(-1), cancellationToken);
        // A DENIED decision powering the decision-explainer demo (analyst lacks publish).
        await AddDecisionAsync("seed-pricing-denied-publish", "pricing-management", "user5.analyst@icis.com", "price.publish", [], DateTimeOffset.UtcNow.AddHours(-6), cancellationToken, allowed: false, denyReason: "NO_MATCHING_GRANT");
    }

    private async Task AddDecisionAsync(string decisionId, string applicationId, string subjectEmail, string action, string[] matchedRoles, DateTimeOffset timestamp, CancellationToken cancellationToken, bool allowed = true, string? denyReason = null)
    {
        if (await dbContext.Decisions.AnyAsync(entity => entity.DecisionId == decisionId, cancellationToken))
        {
            return;
        }

        dbContext.Decisions.Add(new DecisionEntity
        {
            DecisionId = decisionId,
            ApplicationId = applicationId,
            SubjectType = "USER",
            SubjectEmail = subjectEmail,
            ResourceType = "generic",
            ResourceId = null,
            Action = action,
            ContextSnapshot = "{}",
            Allowed = allowed,
            DenyReason = denyReason,
            MatchedRoles = matchedRoles,
            MatchedPermissions = [],
            MatchedPolicies = [],
            VersionsUsed = "{}",
            Timestamp = timestamp,
        });
    }

    private async Task SeedAssignmentAttributesAsync(CancellationToken cancellationToken)
    {
        await AddAssignmentAttributeAsync("pricing-management", "user6.editor@icis.com", "pricing-editor", "markets", "styrene,benzene", "string", cancellationToken);
        await AddAssignmentAttributeAsync("pricing-management", "user6.editor@icis.com", "pricing-editor", "region", "APAC", "string", cancellationToken);
        await AddAssignmentAttributeAsync("pricing-management", "user7.lead@icis.com", "pricing-lead", "maxDeltaPct", "15", "number", cancellationToken);
        await AddAssignmentAttributeAsync("pricing-management", "user23.analyst@icis.com", "pricing-analyst", "markets", "ethylene,propylene", "string", cancellationToken);
        await AddAssignmentAttributeAsync("pricing-management", "user24.editor@icis.com", "pricing-editor", "region", "EMEA", "string", cancellationToken);
        await AddAssignmentAttributeAsync("pricing-management", "user28.lead@icis.com", "pricing-lead", "maxDeltaPct", "25", "number", cancellationToken);
        await AddAssignmentAttributeAsync("lng-edge", "user10.trader@icis.com", "lng-trader", "regions", "APAC,Europe", "string", cancellationToken);
        await AddAssignmentAttributeAsync("lng-edge", "user12.analyst@icis.com", "lng-analyst", "exportQuota", "100000", "number", cancellationToken);
    }

    private async Task AddAssignmentAttributeAsync(string applicationId, string subjectEmail, string roleKey, string name, string value, string valueType, CancellationToken cancellationToken)
    {
        Guid applicationRefId = await ResolveApplicationIdAsync(applicationId, cancellationToken);
        Guid roleRefId = await ResolveRoleIdAsync(applicationRefId, roleKey, cancellationToken);
        AssignmentEntity? assignment = await dbContext.Assignments.FirstOrDefaultAsync(
            entity => entity.ApplicationRefId == applicationRefId && entity.SubjectEmail == subjectEmail && entity.RoleRefId == roleRefId,
            cancellationToken);

        if (assignment is null || await dbContext.AssignmentAttributes.AnyAsync(entity => entity.AssignmentId == assignment.Id && entity.Name == name, cancellationToken))
        {
            return;
        }

        dbContext.AssignmentAttributes.Add(new AssignmentAttributeEntity
        {
            AssignmentId = assignment.Id,
            Name = name,
            Value = valueType == "number" ? value : System.Text.Json.JsonSerializer.Serialize(value),
            ValueType = valueType,
            CreatedBy = SeedActor,
        });
    }

    private async Task SeedPoliciesAsync(CancellationToken cancellationToken)
    {
        await AddPolicyAsync("intelligence-authoring", "allow-publish-when-approved", "article.publish", "ALLOW", "{\"conditions\":[{\"attribute\":\"context.status\",\"operator\":\"eq\",\"value\":\"READY_TO_PUBLISH\"}]}", cancellationToken);
        await AddPolicyAsync("pricing-management", "allow-publish-ready", "price.publish", "ALLOW", "{\"conditions\":[{\"attribute\":\"context.status\",\"operator\":\"eq\",\"value\":\"READY_TO_PUBLISH\"}]}", cancellationToken);
        await AddPolicyAsync("pricing-management", "deny-self-publish", "price.publish", "DENY", "{\"conditions\":[{\"attribute\":\"context.isAuthor\",\"operator\":\"eq\",\"value\":\"true\"}]}", cancellationToken);
        await AddPolicyAsync("pricing-management", "allow-market-scoped-review", "price.review", "ALLOW", "{\"conditions\":[{\"attribute\":\"context.market\",\"operator\":\"in\",\"value\":\"assignment.markets\"}]}", cancellationToken);
        await AddPolicyAsync("pricing-management", "deny-large-correction", "price.correct", "DENY", "{\"conditions\":[{\"attribute\":\"context.priceDeltaPct\",\"operator\":\"gt\",\"value\":\"assignment.maxDeltaPct\"}]}", cancellationToken);
        // A DRAFT policy pending publish — gives the F5 impact-analysis surface something to analyse.
        await AddPolicyAsync("pricing-management", "draft-deny-stale-price-publish", "price.publish", "DENY", "{\"conditions\":[{\"attribute\":\"context.priceAgeMinutes\",\"operator\":\"gt\",\"value\":\"60\"}]}", cancellationToken, state: "DRAFT");
        // References an application reference-data document (reference.approved-markets).
        await AddPolicyAsync("pricing-management", "allow-approved-market-edit", "price.edit", "ALLOW", "{\"conditions\":[{\"attribute\":\"context.market\",\"operator\":\"in\",\"value\":\"reference.approved-markets\"}]}", cancellationToken);
        await AddPolicyAsync("lng-edge", "allow-region-scoped-flows", "flows.view", "ALLOW", "{\"conditions\":[{\"attribute\":\"context.region\",\"operator\":\"in\",\"value\":\"assignment.regions\"}]}", cancellationToken);
        await AddPolicyAsync("lng-edge", "deny-export-over-quota", "data.download", "DENY", "{\"conditions\":[{\"attribute\":\"context.rowsRequested\",\"operator\":\"gt\",\"value\":\"assignment.exportQuota\"}]}", cancellationToken);
        await AddPolicyAsync("lng-edge", "deny-embargoed-region", "commercial.view", "DENY", "{\"conditions\":[{\"attribute\":\"context.region\",\"operator\":\"eq\",\"value\":\"SANCTIONED\"}]}", cancellationToken);
    }

    private async Task AddPolicyAsync(string applicationId, string policyKey, string permissionKey, string effect, string conditionsJson, CancellationToken cancellationToken, string state = "PUBLISHED")
    {
        Guid applicationRefId = await ResolveApplicationIdAsync(applicationId, cancellationToken);
        Guid permissionRefId = await ResolvePermissionIdAsync(applicationRefId, permissionKey, cancellationToken);
        if (await dbContext.Policies.AnyAsync(entity => entity.ApplicationRefId == applicationRefId && entity.PolicyKey == policyKey, cancellationToken))
        {
            return;
        }

        dbContext.Policies.Add(new PolicyEntity
        {
            ApplicationRefId = applicationRefId,
            PolicyKey = policyKey,
            PermissionRefId = permissionRefId,
            Effect = effect,
            Conditions = conditionsJson,
            State = state,
            PublishedAt = state == "PUBLISHED" ? DateTimeOffset.UtcNow.AddDays(-1) : null,
            CreatedBy = SeedActor,
        });
    }

    private async Task SeedSodRulesAsync(CancellationToken cancellationToken)
    {
        // A classic financial toxic combination: whoever can submit a price must not also publish it.
        // pricing-admin holds both grants, so this rule surfaces a ROLE-level violation out of the box.
        await AddSodRuleAsync(
            "pricing-management",
            "segregate-submit-publish",
            "Segregate price submission and publishing",
            "No single role or user should be able to both submit a price and publish it; splitting these duties prevents self-approval of pricing changes.",
            "HIGH",
            "{\"action\":\"submit\"}",
            "{\"action\":\"publish\"}",
            cancellationToken);
    }

    private async Task AddSodRuleAsync(
        string applicationId,
        string ruleKey,
        string name,
        string rationale,
        string severity,
        string matcherAJson,
        string matcherBJson,
        CancellationToken cancellationToken)
    {
        Guid applicationRefId = await ResolveApplicationIdAsync(applicationId, cancellationToken);
        if (await dbContext.SodRules.AnyAsync(entity => entity.ApplicationRefId == applicationRefId && entity.RuleKey == ruleKey, cancellationToken))
        {
            return;
        }

        dbContext.SodRules.Add(new SodRuleEntity
        {
            ApplicationRefId = applicationRefId,
            RuleKey = ruleKey,
            Name = name,
            Rationale = rationale,
            Severity = severity,
            MatcherA = matcherAJson,
            MatcherB = matcherBJson,
            Status = "ACTIVE",
            CreatedBy = SeedActor,
        });
    }

    private async Task SeedReferenceDataAsync(CancellationToken cancellationToken)
    {
        // Reference-data document referenced by the allow-approved-market-edit policy
        // (condition value "reference.approved-markets").
        await AddReferenceDataAsync(
            "pricing-management",
            "approved-markets",
            "Markets pricing editors may edit without additional sign-off.",
            "[\"ethylene\",\"propylene\",\"styrene\",\"benzene\"]",
            cancellationToken);
    }

    private async Task AddReferenceDataAsync(string applicationId, string key, string description, string valueJson, CancellationToken cancellationToken)
    {
        Guid applicationRefId = await ResolveApplicationIdAsync(applicationId, cancellationToken);
        if (await dbContext.ReferenceData.AnyAsync(entity => entity.ApplicationRefId == applicationRefId && entity.Key == key, cancellationToken))
        {
            return;
        }

        dbContext.ReferenceData.Add(new ReferenceDataEntity
        {
            ApplicationRefId = applicationRefId,
            Key = key,
            Description = description,
            Value = valueJson,
            Status = "ACTIVE",
            CreatedBy = SeedActor,
        });
    }

    private async Task SeedReviewCampaignsAsync(CancellationToken cancellationToken)
    {
        // Access-certification (F4) demo: an ACTIVE recertification campaign for pricing-management
        // whose worklist is pre-seeded with each decision state (PENDING / KEEP / REVOKE / NEEDS_INFO).
        Guid applicationRefId = await ResolveApplicationIdAsync("pricing-management", cancellationToken);
        const string campaignName = "Pricing Half-Year Recertification";
        if (await dbContext.ReviewCampaigns.AnyAsync(entity => entity.ApplicationRefId == applicationRefId && entity.Name == campaignName, cancellationToken))
        {
            return;
        }

        var campaign = new ReviewCampaignEntity
        {
            ApplicationRefId = applicationRefId,
            Name = campaignName,
            Status = "ACTIVE",
            DueAt = DateTimeOffset.UtcNow.AddDays(14),
            CreatedBy = SeedActor,
        };
        dbContext.ReviewCampaigns.Add(campaign);
        await dbContext.SaveChangesAsync(cancellationToken);

        await AddReviewItemAsync(campaign.Id, applicationRefId, "user13.reviewer@icis.com", "pricing-analyst", "KEEP", "Actively used in the last week.", cancellationToken);
        await AddReviewItemAsync(campaign.Id, applicationRefId, "user13.reviewer@icis.com", "pricing-editor", "REVOKE", "No usage in the review window.", cancellationToken);
        await AddReviewItemAsync(campaign.Id, applicationRefId, "user13.reviewer@icis.com", "pricing-admin", "NEEDS_INFO", "Privileged grant with no active peers — confirm with owner.", cancellationToken);
        await AddReviewItemAsync(campaign.Id, applicationRefId, "user26.analyst@icis.com", "pricing-analyst", "PENDING", null, cancellationToken);
        await AddReviewItemAsync(campaign.Id, applicationRefId, "user27.editor@icis.com", "pricing-editor", "PENDING", null, cancellationToken);
    }

    private async Task AddReviewItemAsync(Guid campaignRefId, Guid applicationRefId, string subjectEmail, string roleKey, string decision, string? decisionNote, CancellationToken cancellationToken)
    {
        Guid roleRefId = await ResolveRoleIdAsync(applicationRefId, roleKey, cancellationToken);
        AssignmentEntity? assignment = await dbContext.Assignments.FirstOrDefaultAsync(
            entity => entity.ApplicationRefId == applicationRefId && entity.SubjectEmail == subjectEmail && entity.RoleRefId == roleRefId,
            cancellationToken);
        if (assignment is null)
        {
            return;
        }

        bool decided = decision != "PENDING";
        dbContext.ReviewItems.Add(new ReviewItemEntity
        {
            CampaignRefId = campaignRefId,
            AssignmentRefId = assignment.Id,
            SubjectEmail = subjectEmail,
            RoleKey = roleKey,
            Decision = decision,
            DecisionNote = decisionNote,
            DecidedAt = decided ? DateTimeOffset.UtcNow.AddDays(-1) : null,
            DecidedBy = decided ? "admin@local.test" : null,
            CreatedBy = SeedActor,
        });
    }

    private async Task SeedAuditEventsAsync(CancellationToken cancellationToken)
    {
        // A representative pricing-management audit trail so the audit/activity surface is populated
        // from a clean seed. Gated on a sentinel correlation id to stay idempotent.
        const string seedCorrelation = "seed-pricing-audit";
        if (await dbContext.AuditEvents.AnyAsync(entity => entity.CorrelationId == seedCorrelation, cancellationToken))
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        (string EventType, string? Target, string? NewValue, int DaysAgo)[] events =
        [
            ("ROLE_CREATED", null, "{\"roleKey\":\"pricing-admin\",\"privileged\":true}", 30),
            ("POLICY_PUBLISHED", null, "{\"policyKey\":\"allow-publish-ready\",\"state\":\"PUBLISHED\"}", 20),
            ("ASSIGNMENT_CREATED", "user7.lead@icis.com", "{\"roleKey\":\"pricing-lead\"}", 10),
            ("BREAK_GLASS_ACTIVATED", "user21.admin@icis.com", "{\"roleKey\":\"pricing-admin\",\"reason\":\"INC-4821\"}", 2),
            ("ASSIGNMENT_REVOKED", "user8.admin@icis.com", "{\"roleKey\":\"pricing-admin\"}", 1),
        ];

        foreach ((string eventType, string? target, string? newValue, int daysAgo) in events)
        {
            dbContext.AuditEvents.Add(new AuditEventEntity
            {
                EventType = eventType,
                ApplicationId = "pricing-management",
                ActorEmail = "admin@local.test",
                ActorRole = "PlatformSuperAdmin",
                TargetSubjectEmail = target,
                Timestamp = now.AddDays(-daysAgo),
                NewValue = newValue,
                CorrelationId = seedCorrelation,
            });
        }
    }
}
