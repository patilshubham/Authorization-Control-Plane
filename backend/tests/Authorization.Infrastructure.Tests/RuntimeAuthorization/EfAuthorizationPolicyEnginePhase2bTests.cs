using Authorization.Infrastructure.Persistence;
using Authorization.Infrastructure.RuntimeAuthorization;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Infrastructure.Tests.RuntimeAuthorization;

/// <summary>
/// Phase 2b engine behavior: per-application policy combining algorithms, per-policy priority
/// tie-breaking, decision obligations, and stored reference-data resolution via reference.&lt;key&gt;.
/// Uses a hermetic in-memory fixture the tests fully control.
/// </summary>
public sealed class EfAuthorizationPolicyEnginePhase2bTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 10, 0, 0, 0, TimeSpan.Zero);

    // ── Combining algorithms ──────────────────────────────────────────────────

    [Fact]
    public async Task DenyOverrides_IsDefault_AndDenyWinsWhenAllowAndDenyBothMatch()
    {
        await using AuthorizationDbContext db = await SeedAsync("deny-overrides");
        await AddPolicyAsync(db, "allow-read", "ALLOW", UnconditionalConditions);
        await AddPolicyAsync(db, "deny-read", "DENY", UnconditionalConditions);
        var engine = new EfAuthorizationPolicyEngine(db, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(ReadRequest());

        Assert.False(decision.Allowed);
        Assert.Equal("EXPLICIT_DENY", decision.DenyReason);
        Assert.Contains("deny-read", decision.MatchedPolicies);
    }

    [Fact]
    public async Task AllowOverrides_AllowWinsWhenAllowAndDenyBothMatch()
    {
        await using AuthorizationDbContext db = await SeedAsync("allow-overrides");
        await AddPolicyAsync(db, "allow-read", "ALLOW", UnconditionalConditions);
        await AddPolicyAsync(db, "deny-read", "DENY", UnconditionalConditions);
        var engine = new EfAuthorizationPolicyEngine(db, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(ReadRequest());

        Assert.True(decision.Allowed, decision.DenyReason);
        Assert.Contains("allow-read", decision.MatchedPolicies);
    }

    [Fact]
    public async Task FirstApplicable_HighestPriorityDecides_Allow()
    {
        await using AuthorizationDbContext db = await SeedAsync("first-applicable");
        await AddPolicyAsync(db, "allow-read", "ALLOW", UnconditionalConditions, priority: 10);
        await AddPolicyAsync(db, "deny-read", "DENY", UnconditionalConditions, priority: 5);
        var engine = new EfAuthorizationPolicyEngine(db, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(ReadRequest());

        Assert.True(decision.Allowed, decision.DenyReason);
    }

    [Fact]
    public async Task FirstApplicable_HighestPriorityDecides_Deny()
    {
        await using AuthorizationDbContext db = await SeedAsync("first-applicable");
        await AddPolicyAsync(db, "allow-read", "ALLOW", UnconditionalConditions, priority: 5);
        await AddPolicyAsync(db, "deny-read", "DENY", UnconditionalConditions, priority: 10);
        var engine = new EfAuthorizationPolicyEngine(db, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(ReadRequest());

        Assert.False(decision.Allowed);
        Assert.Equal("EXPLICIT_DENY", decision.DenyReason);
    }

    [Fact]
    public async Task FirstApplicable_TieBrokenByOrdinalPolicyKey()
    {
        // Equal priority: the ordinally-first key ("a-allow" < "z-deny") decides under first-applicable.
        await using AuthorizationDbContext db = await SeedAsync("first-applicable");
        await AddPolicyAsync(db, "a-allow", "ALLOW", UnconditionalConditions, priority: 7);
        await AddPolicyAsync(db, "z-deny", "DENY", UnconditionalConditions, priority: 7);
        var engine = new EfAuthorizationPolicyEngine(db, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(ReadRequest());

        Assert.True(decision.Allowed, decision.DenyReason);
    }

    // ── Obligations ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Obligations_ReturnedFromDecidingEffectPolicies()
    {
        await using AuthorizationDbContext db = await SeedAsync("allow-overrides");
        await AddPolicyAsync(db, "allow-read", "ALLOW", UnconditionalConditions,
            obligations: "[\"require_mfa\",{\"id\":\"mask_ssn\",\"value\":\"last4\"}]");
        var engine = new EfAuthorizationPolicyEngine(db, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(ReadRequest());

        Assert.True(decision.Allowed, decision.DenyReason);
        Assert.Collection(
            decision.Obligations,
            o => { Assert.Equal("require_mfa", o.Id); Assert.Null(o.Value); },
            o => { Assert.Equal("mask_ssn", o.Id); Assert.Equal("last4", o.Value); });
    }

    [Fact]
    public async Task Obligations_DedupedById_FirstOccurrenceWins()
    {
        await using AuthorizationDbContext db = await SeedAsync("allow-overrides");
        await AddPolicyAsync(db, "a-allow", "ALLOW", UnconditionalConditions,
            obligations: "[{\"id\":\"require_mfa\",\"value\":\"strong\"}]", priority: 10);
        await AddPolicyAsync(db, "b-allow", "ALLOW", UnconditionalConditions,
            obligations: "[{\"id\":\"require_mfa\",\"value\":\"weak\"}]", priority: 1);
        var engine = new EfAuthorizationPolicyEngine(db, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(ReadRequest());

        Assert.True(decision.Allowed, decision.DenyReason);
        AuthorizeObligation only = Assert.Single(decision.Obligations);
        Assert.Equal("require_mfa", only.Id);
        // Higher-priority policy contributes the winning value.
        Assert.Equal("strong", only.Value);
    }

    [Fact]
    public async Task Obligations_OnlyFromPoliciesMatchingDecisionEffect()
    {
        // Deny wins; the allow policy's obligation must not leak into a deny decision.
        await using AuthorizationDbContext db = await SeedAsync("deny-overrides");
        await AddPolicyAsync(db, "allow-read", "ALLOW", UnconditionalConditions, obligations: "[\"log_allow\"]");
        await AddPolicyAsync(db, "deny-read", "DENY", UnconditionalConditions, obligations: "[\"log_deny\"]");
        var engine = new EfAuthorizationPolicyEngine(db, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(ReadRequest());

        Assert.False(decision.Allowed);
        AuthorizeObligation only = Assert.Single(decision.Obligations);
        Assert.Equal("log_deny", only.Id);
    }

    [Fact]
    public async Task Obligations_MalformedJson_YieldsNoObligations()
    {
        await using AuthorizationDbContext db = await SeedAsync("allow-overrides");
        await AddPolicyAsync(db, "allow-read", "ALLOW", UnconditionalConditions, obligations: "not-json");
        var engine = new EfAuthorizationPolicyEngine(db, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(ReadRequest());

        Assert.True(decision.Allowed, decision.DenyReason);
        Assert.Empty(decision.Obligations);
    }

    // ── Reference data ────────────────────────────────────────────────────────

    [Fact]
    public async Task ReferenceData_InOperator_MembershipFromArrayDocument()
    {
        await using AuthorizationDbContext db = await SeedAsync("deny-overrides");
        await AddReferenceDataAsync(db, "allowed_countries", "[\"US\",\"CA\"]");
        await AddPolicyAsync(db, "allow-read", "ALLOW",
            "{\"conditions\":[{\"attribute\":\"context.country\",\"operator\":\"in\",\"value\":\"reference.allowed_countries\"}]}");
        var engine = new EfAuthorizationPolicyEngine(db, new StaticTimeProvider(Now));

        AuthorizeDecision allowed = await engine.AuthorizeAsync(ReadRequest(new Dictionary<string, object?> { ["country"] = "US" }));
        Assert.True(allowed.Allowed, allowed.DenyReason);

        AuthorizeDecision denied = await engine.AuthorizeAsync(ReadRequest(new Dictionary<string, object?> { ["country"] = "MX" }));
        Assert.False(denied.Allowed);
    }

    [Fact]
    public async Task ReferenceData_ContainsAll_FromArrayDocument()
    {
        await using AuthorizationDbContext db = await SeedAsync("deny-overrides");
        await AddReferenceDataAsync(db, "required_scopes", "[\"read\",\"list\"]");
        await AddPolicyAsync(db, "allow-read", "ALLOW",
            "{\"conditions\":[{\"attribute\":\"context.scopes\",\"operator\":\"containsAll\",\"value\":\"reference.required_scopes\"}]}");
        var engine = new EfAuthorizationPolicyEngine(db, new StaticTimeProvider(Now));

        AuthorizeDecision allowed = await engine.AuthorizeAsync(ReadRequest(new Dictionary<string, object?> { ["scopes"] = "read,list,write" }));
        Assert.True(allowed.Allowed, allowed.DenyReason);

        AuthorizeDecision denied = await engine.AuthorizeAsync(ReadRequest(new Dictionary<string, object?> { ["scopes"] = "read" }));
        Assert.False(denied.Allowed);
    }

    [Fact]
    public async Task ReferenceData_NestedPath_ResolvesLeafValue()
    {
        await using AuthorizationDbContext db = await SeedAsync("deny-overrides");
        await AddReferenceDataAsync(db, "limits", "{\"maxAmount\":\"1000\"}");
        await AddPolicyAsync(db, "allow-read", "ALLOW",
            "{\"conditions\":[{\"attribute\":\"context.amount\",\"operator\":\"lte\",\"value\":\"reference.limits.maxAmount\"}]}");
        var engine = new EfAuthorizationPolicyEngine(db, new StaticTimeProvider(Now));

        AuthorizeDecision allowed = await engine.AuthorizeAsync(ReadRequest(new Dictionary<string, object?> { ["amount"] = 500 }));
        Assert.True(allowed.Allowed, allowed.DenyReason);

        AuthorizeDecision denied = await engine.AuthorizeAsync(ReadRequest(new Dictionary<string, object?> { ["amount"] = 5000 }));
        Assert.False(denied.Allowed);
    }

    [Fact]
    public async Task ReferenceData_ArchivedDocument_IsNotResolved_FailsClosed()
    {
        await using AuthorizationDbContext db = await SeedAsync("deny-overrides");
        await AddReferenceDataAsync(db, "allowed_countries", "[\"US\"]", status: "ARCHIVED");
        await AddPolicyAsync(db, "allow-read", "ALLOW",
            "{\"conditions\":[{\"attribute\":\"context.country\",\"operator\":\"in\",\"value\":\"reference.allowed_countries\"}]}");
        var engine = new EfAuthorizationPolicyEngine(db, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(ReadRequest(new Dictionary<string, object?> { ["country"] = "US" }));

        // The reference document is archived, so it cannot be resolved: the guard fails closed.
        Assert.False(decision.Allowed);
        Assert.Equal("MISSING_CONTEXT", decision.DenyReason);
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private const string UnconditionalConditions = "{\"conditions\":[]}";

    private static AuthorizeRequest ReadRequest(IReadOnlyDictionary<string, object?>? context = null)
    {
        return new AuthorizeRequest(
            "docs-app",
            "USER",
            "reader@local.test",
            "doc",
            "doc-1",
            "read",
            context ?? new Dictionary<string, object?>());
    }

    private static async Task<AuthorizationDbContext> SeedAsync(string combiningAlgorithm)
    {
        var options = new DbContextOptionsBuilder<AuthorizationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AuthorizationDbContext(options);
        DateTimeOffset seededAt = Now.AddDays(-1);

        var tenant = new TenantEntity { TenantId = "t", Name = "T", Status = "ACTIVE", CreatedBy = "test" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var app = new ApplicationEntity
        {
            ApplicationId = "docs-app",
            Name = "Docs",
            TenantRefId = tenant.Id,
            RiskLevel = "MEDIUM",
            Status = "ACTIVE",
            PolicyCombiningAlgorithm = combiningAlgorithm,
            CreatedBy = "test",
        };
        db.Applications.Add(app);
        await db.SaveChangesAsync();

        var role = new RoleEntity { ApplicationRefId = app.Id, RoleKey = "reader", Name = "Reader", Status = "ACTIVE", CreatedBy = "test" };
        db.Roles.Add(role);
        var permission = new PermissionEntity { ApplicationRefId = app.Id, PermissionKey = "doc.read", Resource = "doc", Action = "read", Status = "ACTIVE", CreatedBy = "test" };
        db.Permissions.Add(permission);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermissionEntity
        {
            ApplicationRefId = app.Id,
            RoleRefId = role.Id,
            PermissionRefId = permission.Id,
            State = "PUBLISHED",
            ValidFrom = seededAt,
            PublishedAt = seededAt,
            CreatedBy = "test",
        });
        db.Assignments.Add(new AssignmentEntity
        {
            ApplicationRefId = app.Id,
            SubjectType = "USER",
            SubjectEmail = "reader@local.test",
            RoleRefId = role.Id,
            ValidFrom = seededAt,
            State = "ACTIVE",
            Source = "MANUAL",
            CreatedBy = "test",
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task AddPolicyAsync(
        AuthorizationDbContext db,
        string policyKey,
        string effect,
        string conditions,
        int priority = 0,
        string obligations = "[]")
    {
        Guid appId = await db.Applications.Where(a => a.ApplicationId == "docs-app").Select(a => a.Id).SingleAsync();
        Guid permId = await db.Permissions.Where(p => p.ApplicationRefId == appId && p.PermissionKey == "doc.read").Select(p => p.Id).SingleAsync();
        db.Policies.Add(new PolicyEntity
        {
            ApplicationRefId = appId,
            PolicyKey = policyKey,
            PermissionRefId = permId,
            Effect = effect,
            State = "PUBLISHED",
            PublishedAt = Now,
            Conditions = conditions,
            Priority = priority,
            Obligations = obligations,
            CreatedBy = "test",
        });
        await db.SaveChangesAsync();
    }

    private static async Task AddReferenceDataAsync(
        AuthorizationDbContext db,
        string key,
        string value,
        string status = "ACTIVE")
    {
        Guid appId = await db.Applications.Where(a => a.ApplicationId == "docs-app").Select(a => a.Id).SingleAsync();
        db.ReferenceData.Add(new ReferenceDataEntity
        {
            ApplicationRefId = appId,
            Key = key,
            Value = value,
            Status = status,
            CreatedBy = "test",
        });
        await db.SaveChangesAsync();
    }

    private sealed class StaticTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset utcNow;
        public StaticTimeProvider(DateTimeOffset utcNow) => this.utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
