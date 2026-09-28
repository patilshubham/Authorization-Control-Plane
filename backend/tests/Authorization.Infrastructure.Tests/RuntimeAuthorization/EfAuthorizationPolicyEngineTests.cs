using Authorization.Infrastructure.Persistence;
using Authorization.Infrastructure.RuntimeAuthorization;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Authorization.Infrastructure.Tests.RuntimeAuthorization;

public sealed class EfAuthorizationPolicyEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 10, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AuthorizeAsync_Allows_WhenRolePermissionAndAllowPolicyMatch()
    {
        await using AuthorizationDbContext dbContext = await CreateSeededDbContextAsync();
        var engine = new EfAuthorizationPolicyEngine(dbContext, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(FinanceRequest(new Dictionary<string, object?>
        {
            ["amount"] = 5000,
        }));

        Assert.True(decision.Allowed);
        Assert.Null(decision.DenyReason);
        Assert.Contains("invoice.approve", decision.MatchedPermissions);
        Assert.Contains("invoice-approver", decision.MatchedRoles);
    }

    [Fact]
    public async Task AuthorizeAsync_Denies_WhenNoPermissionMappingExists()
    {
        await using AuthorizationDbContext dbContext = await CreateSeededDbContextAsync();
        var engine = new EfAuthorizationPolicyEngine(dbContext, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(new AuthorizeRequest(
            "finance-app",
            "USER",
            "bob@local.test",
            "payment",
            "payment-1",
            "release",
            new Dictionary<string, object?>()));

        Assert.False(decision.Allowed);
        Assert.Equal("PERMISSION_NOT_GRANTED", decision.DenyReason);
    }

    [Fact]
    public async Task AuthorizeAsync_DenyPolicyOverridesMatchingAllowPolicy()
    {
        await using AuthorizationDbContext dbContext = await CreateSeededDbContextAsync();
        var engine = new EfAuthorizationPolicyEngine(dbContext, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(FinanceRequest(new Dictionary<string, object?>
        {
            ["amount"] = 5000,
            ["vendorRisk"] = "HIGH"
        }));

        Assert.False(decision.Allowed);
        Assert.Equal("EXPLICIT_DENY", decision.DenyReason);
        Assert.Contains("deny-invoice-approval-high-risk-vendor", decision.MatchedPolicies);
    }

    [Fact]
    public async Task AuthorizeAsync_Denies_WhenRequiredContextIsMissing()
    {
        await using AuthorizationDbContext dbContext = await CreateSeededDbContextAsync();
        var engine = new EfAuthorizationPolicyEngine(dbContext, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(FinanceRequest(new Dictionary<string, object?>
        {
        }));

        Assert.False(decision.Allowed);
        Assert.Equal("MISSING_CONTEXT", decision.DenyReason);
    }

    [Fact]
    public async Task AuthorizeAsync_Denies_WhenAssignmentIsRevoked()
    {
        await using AuthorizationDbContext dbContext = await CreateSeededDbContextAsync();
        AssignmentEntity assignment = await FinanceManagerAssignmentAsync(dbContext);
        assignment.State = "REVOKED";
        assignment.RevokedAt = Now.AddMinutes(-1);
        await dbContext.SaveChangesAsync();
        var engine = new EfAuthorizationPolicyEngine(dbContext, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(FinanceRequest(new Dictionary<string, object?>
        {
            ["amount"] = 5000,
        }));

        Assert.False(decision.Allowed);
        Assert.Equal("ASSIGNMENT_REVOKED", decision.DenyReason);
    }

    [Fact]
    public async Task AuthorizeAsync_Denies_WhenAssignmentIsExpired()
    {
        await using AuthorizationDbContext dbContext = await CreateSeededDbContextAsync();
        AssignmentEntity assignment = await FinanceManagerAssignmentAsync(dbContext);
        assignment.ValidUntil = Now.AddMinutes(-1);
        await dbContext.SaveChangesAsync();
        var engine = new EfAuthorizationPolicyEngine(dbContext, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(FinanceRequest(new Dictionary<string, object?>
        {
            ["amount"] = 5000,
        }));

        Assert.False(decision.Allowed);
        Assert.Equal("ASSIGNMENT_EXPIRED", decision.DenyReason);
    }

    [Theory]
    [InlineData("contains", "context.dept", "finance", "dept", "finance-team", true)]
    [InlineData("contains", "context.dept", "finance", "dept", "hr-team", false)]
    [InlineData("startsWith", "context.code", "inv", "code", "invoice-42", true)]
    [InlineData("startsWith", "context.code", "inv", "code", "payment-1", false)]
    [InlineData("endsWith", "context.env", "-prod", "env", "us-prod", true)]
    [InlineData("endsWith", "context.env", "-prod", "env", "us-dev", false)]
    [InlineData("exists", "context.region", "", "region", "us", true)]
    [InlineData("notExists", "context.flag", "", "flag", "", true)]
    [InlineData("gte", "context.amount", "5", "amount", "10", true)]
    [InlineData("gte", "context.amount", "5", "amount", "3", false)]
    [InlineData("lt", "context.amount", "5", "amount", "3", true)]
    [InlineData("lt", "context.amount", "5", "amount", "10", false)]
    public async Task AuthorizeAsync_EvaluatesComparisonOperators(
        string operatorName,
        string attribute,
        string expectedValue,
        string contextKey,
        string contextValue,
        bool expectedAllowed)
    {
        await using AuthorizationDbContext dbContext = await CreateSeededDbContextAsync();
        Guid crmAppId = await dbContext.Applications.Where(a => a.ApplicationId == "crm-app").Select(a => a.Id).SingleAsync();
        PolicyEntity policy = await dbContext.Policies.SingleAsync(entity =>
            entity.ApplicationRefId == crmAppId && entity.PolicyKey == "allow-customer-update");
        policy.Conditions = $"{{\"conditions\":[{{\"attribute\":\"{attribute}\",\"operator\":\"{operatorName}\",\"value\":\"{expectedValue}\"}}]}}";
        await dbContext.SaveChangesAsync();
        var engine = new EfAuthorizationPolicyEngine(dbContext, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(new AuthorizeRequest(
            "crm-app",
            "USER",
            "alice@local.test",
            "customer",
            "customer-1",
            "update",
            new Dictionary<string, object?> { [contextKey] = contextValue }));

        Assert.Equal(expectedAllowed, decision.Allowed);
    }

    // ── RBAC-permissive model: role grant authorizes unless a guardrail actively denies ──

    [Fact]
    public async Task AuthorizeAsync_Allows_WhenPermissionGrantedAndNoPoliciesExist()
    {
        // Case 1 — the permission is granted via role but has no published policies at all.
        await using AuthorizationDbContext dbContext = await CreateSeededDbContextAsync();
        await GrantPaymentReleaseAsync(dbContext);
        var engine = new EfAuthorizationPolicyEngine(dbContext, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(PaymentReleaseRequest(new Dictionary<string, object?>()));

        Assert.True(decision.Allowed, decision.DenyReason);
        Assert.Null(decision.DenyReason);
        Assert.Contains("payment.release", decision.MatchedPermissions);
        Assert.Empty(decision.MatchedPolicies);
    }

    [Fact]
    public async Task AuthorizeAsync_Allows_WhenOnlyDenyPoliciesExistAndNoneMatch()
    {
        // Case 2b — only a DENY guardrail exists; the request does not trigger it, so the RBAC
        // baseline authorizes the action.
        await using AuthorizationDbContext dbContext = await CreateSeededDbContextAsync();
        Guid permId = await GrantPaymentReleaseAsync(dbContext);
        await AddPaymentPolicyAsync(dbContext, permId, "deny-large-payment", "DENY", "{\"conditions\":[{\"attribute\":\"context.amount\",\"operator\":\"gt\",\"value\":\"1000\"}]}");
        var engine = new EfAuthorizationPolicyEngine(dbContext, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(PaymentReleaseRequest(new Dictionary<string, object?> { ["amount"] = 10 }));

        Assert.True(decision.Allowed, decision.DenyReason);
        Assert.Empty(decision.MatchedPolicies);
    }

    [Fact]
    public async Task AuthorizeAsync_Denies_WhenOnlyDenyPolicyExistsButRequiredContextMissing()
    {
        // Case 2c — only a DENY guardrail exists but its required context is absent, so the guard
        // cannot evaluate. Fail closed with MISSING_CONTEXT (consistent with allow-policy case 3c).
        await using AuthorizationDbContext dbContext = await CreateSeededDbContextAsync();
        Guid permId = await GrantPaymentReleaseAsync(dbContext);
        await AddPaymentPolicyAsync(dbContext, permId, "deny-large-payment", "DENY", "{\"conditions\":[{\"attribute\":\"context.amount\",\"operator\":\"gt\",\"value\":\"1000\"}]}");
        var engine = new EfAuthorizationPolicyEngine(dbContext, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(PaymentReleaseRequest(new Dictionary<string, object?>()));

        Assert.False(decision.Allowed);
        Assert.Equal("MISSING_CONTEXT", decision.DenyReason);
    }

    [Fact]
    public async Task AuthorizeAsync_Denies_WhenUnconditionalDenyPolicyExists()
    {
        // Case 2d — an unconditional DENY acts as a kill-switch that always fires.
        await using AuthorizationDbContext dbContext = await CreateSeededDbContextAsync();
        Guid permId = await GrantPaymentReleaseAsync(dbContext);
        await AddPaymentPolicyAsync(dbContext, permId, "deny-all-payment", "DENY", "{\"conditions\":[]}");
        var engine = new EfAuthorizationPolicyEngine(dbContext, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(PaymentReleaseRequest(new Dictionary<string, object?> { ["amount"] = 10 }));

        Assert.False(decision.Allowed);
        Assert.Equal("EXPLICIT_DENY", decision.DenyReason);
        Assert.Contains("deny-all-payment", decision.MatchedPolicies);
    }

    private static AuthorizeRequest FinanceRequest(IReadOnlyDictionary<string, object?> context)
    {
        return new AuthorizeRequest(
            "finance-app",
            "USER",
            "bob@local.test",
            "invoice",
            "invoice-1",
            "approve",
            context);
    }

    private static AuthorizeRequest PaymentReleaseRequest(IReadOnlyDictionary<string, object?> context)
    {
        return new AuthorizeRequest(
            "finance-app",
            "USER",
            "bob@local.test",
            "payment",
            "payment-1",
            "release",
            context);
    }

    // Grants the otherwise-unmapped payment.release permission to bob's invoice-approver role so
    // policy-layer behavior can be exercised without disturbing the shared fixture's other tests.
    private static async Task<Guid> GrantPaymentReleaseAsync(AuthorizationDbContext dbContext)
    {
        Guid appId = await AppIdAsync(dbContext, "finance-app");
        Guid roleId = await RoleIdAsync(dbContext, appId, "invoice-approver");
        Guid permId = await PermissionIdAsync(dbContext, appId, "payment.release");
        dbContext.RolePermissions.Add(new RolePermissionEntity
        {
            ApplicationRefId = appId,
            RoleRefId = roleId,
            PermissionRefId = permId,
            State = "PUBLISHED",
            ValidFrom = Now.AddDays(-1),
            PublishedAt = Now.AddDays(-1),
            CreatedBy = "test",
        });
        await dbContext.SaveChangesAsync();
        return permId;
    }

    private static async Task AddPaymentPolicyAsync(AuthorizationDbContext dbContext, Guid permissionRefId, string policyKey, string effect, string conditions)
    {
        Guid appId = await AppIdAsync(dbContext, "finance-app");
        dbContext.Policies.Add(new PolicyEntity
        {
            ApplicationRefId = appId,
            PolicyKey = policyKey,
            PermissionRefId = permissionRefId,
            Effect = effect,
            State = "PUBLISHED",
            PublishedAt = Now,
            Conditions = conditions,
            CreatedBy = "test",
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task<AuthorizationDbContext> CreateSeededDbContextAsync()
    {
        var options = new DbContextOptionsBuilder<AuthorizationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var dbContext = new AuthorizationDbContext(options);
        await SeedEngineFixtureAsync(dbContext);
        return dbContext;
    }

    // Hermetic fixture for the policy-engine unit tests. Intentionally decoupled from the
    // production LocalDevelopmentSeeder (which seeds the ICIS business domain) so these tests
    // exercise the engine against a stable, minimal finance/crm dataset they fully control.
    private static async Task SeedEngineFixtureAsync(AuthorizationDbContext dbContext)
    {
        DateTimeOffset seededAt = Now.AddDays(-1);

        var tenant = new TenantEntity { TenantId = "test-tenant", Name = "Test Tenant", Status = "ACTIVE", CreatedBy = "test" };
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesAsync();

        var financeApp = new ApplicationEntity { ApplicationId = "finance-app", Name = "Finance", TenantRefId = tenant.Id, RiskLevel = "HIGH", Status = "ACTIVE", CreatedBy = "test" };
        var crmApp = new ApplicationEntity { ApplicationId = "crm-app", Name = "CRM", TenantRefId = tenant.Id, RiskLevel = "MEDIUM", Status = "ACTIVE", CreatedBy = "test" };
        dbContext.Applications.AddRange(financeApp, crmApp);
        await dbContext.SaveChangesAsync();

        var invoiceApprover = new RoleEntity { ApplicationRefId = financeApp.Id, RoleKey = "invoice-approver", Name = "Invoice Approver", Status = "ACTIVE", CreatedBy = "test" };
        var invoiceViewer = new RoleEntity { ApplicationRefId = financeApp.Id, RoleKey = "invoice-viewer", Name = "Invoice Viewer", Status = "ACTIVE", CreatedBy = "test" };
        var customerManager = new RoleEntity { ApplicationRefId = crmApp.Id, RoleKey = "customer-manager", Name = "Customer Manager", Status = "ACTIVE", CreatedBy = "test" };
        dbContext.Roles.AddRange(invoiceApprover, invoiceViewer, customerManager);

        var invoiceApprove = new PermissionEntity { ApplicationRefId = financeApp.Id, PermissionKey = "invoice.approve", Resource = "invoice", Action = "approve", Status = "ACTIVE", CreatedBy = "test" };
        // Present but intentionally left unmapped so a request for it yields PERMISSION_NOT_GRANTED.
        var paymentRelease = new PermissionEntity { ApplicationRefId = financeApp.Id, PermissionKey = "payment.release", Resource = "payment", Action = "release", Status = "ACTIVE", CreatedBy = "test" };
        var customerUpdate = new PermissionEntity { ApplicationRefId = crmApp.Id, PermissionKey = "customer.update", Resource = "customer", Action = "update", Status = "ACTIVE", CreatedBy = "test" };
        dbContext.Permissions.AddRange(invoiceApprove, paymentRelease, customerUpdate);
        await dbContext.SaveChangesAsync();

        dbContext.RolePermissions.AddRange(
            new RolePermissionEntity { ApplicationRefId = financeApp.Id, RoleRefId = invoiceApprover.Id, PermissionRefId = invoiceApprove.Id, State = "PUBLISHED", ValidFrom = seededAt, PublishedAt = seededAt, CreatedBy = "test" },
            new RolePermissionEntity { ApplicationRefId = crmApp.Id, RoleRefId = customerManager.Id, PermissionRefId = customerUpdate.Id, State = "PUBLISHED", ValidFrom = seededAt, PublishedAt = seededAt, CreatedBy = "test" });

        dbContext.Assignments.AddRange(
            new AssignmentEntity { ApplicationRefId = financeApp.Id, SubjectType = "USER", SubjectEmail = "bob@local.test", RoleRefId = invoiceApprover.Id, ValidFrom = seededAt, State = "ACTIVE", Source = "MANUAL", CreatedBy = "test" },
            new AssignmentEntity { ApplicationRefId = crmApp.Id, SubjectType = "USER", SubjectEmail = "alice@local.test", RoleRefId = customerManager.Id, ValidFrom = seededAt, State = "ACTIVE", Source = "MANUAL", CreatedBy = "test" });

        dbContext.Policies.AddRange(
            new PolicyEntity { ApplicationRefId = financeApp.Id, PolicyKey = "allow-invoice-approval", PermissionRefId = invoiceApprove.Id, Effect = "ALLOW", Conditions = "{\"conditions\":[{\"attribute\":\"context.amount\",\"operator\":\"gte\",\"value\":\"0\"}]}", State = "PUBLISHED", PublishedAt = seededAt, CreatedBy = "test" },
            new PolicyEntity { ApplicationRefId = financeApp.Id, PolicyKey = "deny-invoice-approval-high-risk-vendor", PermissionRefId = invoiceApprove.Id, Effect = "DENY", Conditions = "{\"conditions\":[{\"attribute\":\"context.vendorRisk\",\"operator\":\"eq\",\"value\":\"HIGH\"}]}", State = "PUBLISHED", PublishedAt = seededAt, CreatedBy = "test" },
            new PolicyEntity { ApplicationRefId = crmApp.Id, PolicyKey = "allow-customer-update", PermissionRefId = customerUpdate.Id, Effect = "ALLOW", Conditions = "{\"conditions\":[]}", State = "PUBLISHED", PublishedAt = seededAt, CreatedBy = "test" });

        await dbContext.SaveChangesAsync();
    }

    private static async Task<AssignmentEntity> FinanceManagerAssignmentAsync(AuthorizationDbContext dbContext)
    {
        Guid appId = await AppIdAsync(dbContext, "finance-app");
        Guid roleId = await RoleIdAsync(dbContext, appId, "invoice-approver");
        return await dbContext.Assignments.SingleAsync(assignment =>
            assignment.ApplicationRefId == appId
            && assignment.SubjectEmail == "bob@local.test"
            && assignment.RoleRefId == roleId);
    }

    private static async Task<Guid> AppIdAsync(AuthorizationDbContext dbContext, string applicationId)
    {
        return await dbContext.Applications.Where(a => a.ApplicationId == applicationId).Select(a => a.Id).SingleAsync();
    }

    private static async Task<Guid> RoleIdAsync(AuthorizationDbContext dbContext, Guid applicationRefId, string roleKey)
    {
        return await dbContext.Roles.Where(r => r.ApplicationRefId == applicationRefId && r.RoleKey == roleKey).Select(r => r.Id).SingleAsync();
    }

    private static async Task<Guid> PermissionIdAsync(AuthorizationDbContext dbContext, Guid applicationRefId, string permissionKey)
    {
        return await dbContext.Permissions.Where(p => p.ApplicationRefId == applicationRefId && p.PermissionKey == permissionKey).Select(p => p.Id).SingleAsync();
    }

    private static async Task AddFinancePolicyAsync(AuthorizationDbContext dbContext, string policyKey, string effect, string conditions)
    {
        Guid appId = await AppIdAsync(dbContext, "finance-app");
        Guid permId = await PermissionIdAsync(dbContext, appId, "invoice.approve");
        dbContext.Policies.Add(new PolicyEntity
        {
            ApplicationRefId = appId,
            PolicyKey = policyKey,
            PermissionRefId = permId,
            Effect = effect,
            State = "PUBLISHED",
            PublishedAt = Now,
            Conditions = conditions,
            CreatedBy = "test",
        });
        await dbContext.SaveChangesAsync();
    }

    // ── Enhanced operators: typed comparison, dates, regex, collections, boolean ──

    [Theory]
    // Date-aware ordering (previously failed because dates are not decimals).
    [InlineData("gt", "context.d", "2026-01-01", "d", "2026-06-01", true)]
    [InlineData("gt", "context.d", "2026-06-01", "d", "2026-01-01", false)]
    [InlineData("before", "context.d", "2026-12-31", "d", "2026-06-01", true)]
    [InlineData("before", "context.d", "2026-01-01", "d", "2026-06-01", false)]
    [InlineData("after", "context.d", "2026-01-01", "d", "2026-06-01", true)]
    // Inclusive ranges for numbers and dates.
    [InlineData("between", "context.amount", "10,20", "amount", "15", true)]
    [InlineData("between", "context.amount", "10,20", "amount", "25", false)]
    [InlineData("between", "context.d", "2026-01-01,2026-12-31", "d", "2026-06-01", true)]
    // Text.
    [InlineData("notContains", "context.code", "x", "code", "abc", true)]
    [InlineData("notContains", "context.code", "b", "code", "abc", false)]
    [InlineData("matches", "context.code", "^inv-[0-9]+$", "code", "inv-42", true)]
    [InlineData("matches", "context.code", "^inv-[0-9]+$", "code", "pay-1", false)]
    [InlineData("notMatches", "context.code", "^inv-[0-9]+$", "code", "pay-1", true)]
    // Collections.
    [InlineData("containsAny", "context.tags", "a,b", "tags", "b,c", true)]
    [InlineData("containsAny", "context.tags", "a,b", "tags", "x,y", false)]
    [InlineData("containsAll", "context.tags", "a,b", "tags", "a,b,c", true)]
    [InlineData("containsAll", "context.tags", "a,b", "tags", "a,c", false)]
    // Boolean.
    [InlineData("isTrue", "context.flag", "", "flag", "true", true)]
    [InlineData("isTrue", "context.flag", "", "flag", "false", false)]
    [InlineData("isFalse", "context.flag", "", "flag", "false", true)]
    public async Task AuthorizeAsync_EvaluatesEnhancedOperators(
        string operatorName,
        string attribute,
        string expectedValue,
        string contextKey,
        string contextValue,
        bool expectedAllowed)
    {
        string conditions = $"{{\"conditions\":[{{\"attribute\":\"{attribute}\",\"operator\":\"{operatorName}\",\"value\":\"{expectedValue}\"}}]}}";
        AuthorizeDecision decision = await EvaluateCustomerUpdateAsync(
            conditions,
            new Dictionary<string, object?> { [contextKey] = contextValue });

        Assert.Equal(expectedAllowed, decision.Allowed);
    }

    [Fact]
    public async Task AuthorizeAsync_MatchNone_AppliesOnlyWhenNoChildMatches()
    {
        const string conditions = "{\"match\":\"none\",\"conditions\":[{\"attribute\":\"context.amount\",\"operator\":\"gt\",\"value\":\"100\"}]}";

        AuthorizeDecision allowed = await EvaluateCustomerUpdateAsync(conditions, new Dictionary<string, object?> { ["amount"] = 50 });
        Assert.True(allowed.Allowed, allowed.DenyReason);

        AuthorizeDecision denied = await EvaluateCustomerUpdateAsync(conditions, new Dictionary<string, object?> { ["amount"] = 200 });
        Assert.False(denied.Allowed);
    }

    [Fact]
    public async Task AuthorizeAsync_ResolvesNestedContextPath()
    {
        using JsonDocument document = JsonDocument.Parse("{\"dept\":\"finance\"}");
        JsonElement user = document.RootElement.Clone();
        const string conditions = "{\"conditions\":[{\"attribute\":\"context.user.dept\",\"operator\":\"eq\",\"value\":\"finance\"}]}";

        AuthorizeDecision decision = await EvaluateCustomerUpdateAsync(conditions, new Dictionary<string, object?> { ["user"] = user });

        Assert.True(decision.Allowed, decision.DenyReason);
    }

    [Fact]
    public async Task AuthorizeAsync_ResolvesSystemNowInExpectedValue()
    {
        // deadline (2027) is after the fixed evaluation clock (Now = 2026-07-10).
        const string conditions = "{\"conditions\":[{\"attribute\":\"context.deadline\",\"operator\":\"after\",\"value\":\"system.now\"}]}";

        AuthorizeDecision decision = await EvaluateCustomerUpdateAsync(conditions, new Dictionary<string, object?> { ["deadline"] = "2027-01-01" });

        Assert.True(decision.Allowed, decision.DenyReason);
    }

    [Fact]
    public async Task AuthorizeAsync_ResolvesSystemAttribute()
    {
        const string conditions = "{\"conditions\":[{\"attribute\":\"system.date\",\"operator\":\"eq\",\"value\":\"2026-07-10\"}]}";

        AuthorizeDecision decision = await EvaluateCustomerUpdateAsync(conditions, new Dictionary<string, object?>());

        Assert.True(decision.Allowed, decision.DenyReason);
    }

    [Fact]
    public async Task AuthorizeAsync_CollectionOperator_HandlesJsonArrayContext()
    {
        using JsonDocument document = JsonDocument.Parse("[\"a\",\"b\",\"c\"]");
        JsonElement tags = document.RootElement.Clone();
        const string conditions = "{\"conditions\":[{\"attribute\":\"context.tags\",\"operator\":\"containsAll\",\"value\":\"a,b\"}]}";

        AuthorizeDecision decision = await EvaluateCustomerUpdateAsync(conditions, new Dictionary<string, object?> { ["tags"] = tags });

        Assert.True(decision.Allowed, decision.DenyReason);
    }

    // Rewrites the single seeded CRM allow-policy's conditions and evaluates a customer.update
    // request for alice, who holds the granting role. The lone ALLOW policy fully determines the
    // outcome, so this isolates leaf/group evaluation behavior.
    private static async Task<AuthorizeDecision> EvaluateCustomerUpdateAsync(string conditionsJson, IReadOnlyDictionary<string, object?> context)
    {
        await using AuthorizationDbContext dbContext = await CreateSeededDbContextAsync();
        Guid crmAppId = await dbContext.Applications.Where(a => a.ApplicationId == "crm-app").Select(a => a.Id).SingleAsync();
        PolicyEntity policy = await dbContext.Policies.SingleAsync(entity =>
            entity.ApplicationRefId == crmAppId && entity.PolicyKey == "allow-customer-update");
        policy.Conditions = conditionsJson;
        await dbContext.SaveChangesAsync();
        var engine = new EfAuthorizationPolicyEngine(dbContext, new StaticTimeProvider(Now));

        return await engine.AuthorizeAsync(new AuthorizeRequest(
            "crm-app",
            "USER",
            "alice@local.test",
            "customer",
            "customer-1",
            "update",
            context));
    }

    private sealed class StaticTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset utcNow;

        public StaticTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }

    // ── Regression: over-deny when subject has one revoked AND one active assignment ──

    [Fact]
    public async Task AuthorizeAsync_Allows_WhenSubjectHasOneRevokedAndOneActiveAssignment()
    {
        await using AuthorizationDbContext dbContext = await CreateSeededDbContextAsync();
        // The seeded assignment for bob is invoice-approver; add a second (revoked) assignment on a different role.
        Guid financeAppId = await AppIdAsync(dbContext, "finance-app");
        Guid viewerRoleId = await RoleIdAsync(dbContext, financeAppId, "invoice-viewer");
        dbContext.Assignments.Add(new AssignmentEntity
        {
            ApplicationRefId = financeAppId,
            SubjectType = "USER",
            SubjectEmail = "bob@local.test",
            RoleRefId = viewerRoleId,
            State = "REVOKED",
            RevokedAt = Now.AddDays(-7),
            ValidFrom = Now.AddDays(-30),
            CreatedBy = "test",
        });
        await dbContext.SaveChangesAsync();
        var engine = new EfAuthorizationPolicyEngine(dbContext, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(FinanceRequest(new Dictionary<string, object?>
        {
            ["amount"] = 5000,
        }));

        // Active invoice-approver assignment should still yield an ALLOW.
        Assert.True(decision.Allowed, decision.DenyReason);
    }

    [Fact]
    public async Task AuthorizeAsync_Denies_WhenAllAssignmentsRevoked()
    {
        await using AuthorizationDbContext dbContext = await CreateSeededDbContextAsync();
        AssignmentEntity assignment = await FinanceManagerAssignmentAsync(dbContext);
        assignment.State = "REVOKED";
        assignment.RevokedAt = Now.AddMinutes(-1);
        await dbContext.SaveChangesAsync();
        var engine = new EfAuthorizationPolicyEngine(dbContext, new StaticTimeProvider(Now));

        AuthorizeDecision decision = await engine.AuthorizeAsync(FinanceRequest(new Dictionary<string, object?>
        {
            ["amount"] = 5000,
        }));

        Assert.False(decision.Allowed);
        Assert.Equal("ASSIGNMENT_REVOKED", decision.DenyReason);
    }

    // ── numeric context value that cannot be parsed should not throw ──

    [Fact]
    public async Task AuthorizeAsync_DeniesWithMissingContext_WhenNumericOperatorReceivesNonNumericValue()
    {
        await using AuthorizationDbContext dbContext = await CreateSeededDbContextAsync();
        var engine = new EfAuthorizationPolicyEngine(dbContext, new StaticTimeProvider(Now));

        // Passing a non-numeric string where the ALLOW policy expects a numeric 'amount' should not throw.
        AuthorizeDecision decision = await engine.AuthorizeAsync(FinanceRequest(new Dictionary<string, object?>
        {
            ["amount"] = "not-a-number",
        }));

        // Policy conditions fail gracefully: numeric comparisons return false → no ALLOW match → denied.
        Assert.False(decision.Allowed);
        Assert.NotNull(decision.DenyReason);
    }

    // ── notIn operator ──

    [Fact]
    public async Task PolicyValidation_Allows_notInOperatorInConditions()
    {
        await using AuthorizationDbContext dbContext = await CreateSeededDbContextAsync();
        // Seed a policy that uses notIn and publish it to test the engine path.
        // We exercise it through the condition JSON that the engine evaluates.
        // Deny if vendorRisk is "LOW" — use notIn("HIGH,CRITICAL") variant.
        await AddFinancePolicyAsync(dbContext, "notIn-test-policy", "DENY", "{\"conditions\":[{\"attribute\":\"context.vendorRisk\",\"operator\":\"notIn\",\"value\":\"LOW,MEDIUM\"}]}");
        var engine = new EfAuthorizationPolicyEngine(dbContext, new StaticTimeProvider(Now));

        // vendorRisk = "HIGH" is NOT in "LOW,MEDIUM" → notIn = true → DENY fires.
        AuthorizeDecision denyDecision = await engine.AuthorizeAsync(FinanceRequest(new Dictionary<string, object?>
        {
            ["amount"] = 5000,
            ["vendorRisk"] = "HIGH",
        }));

        // vendorRisk = "LOW" IS in "LOW,MEDIUM" → notIn = false → DENY does NOT fire.
        AuthorizeDecision allowDecision = await engine.AuthorizeAsync(FinanceRequest(new Dictionary<string, object?>
        {
            ["amount"] = 5000,
            ["vendorRisk"] = "LOW",
        }));

        Assert.False(denyDecision.Allowed);
        Assert.Equal("EXPLICIT_DENY", denyDecision.DenyReason);
        Assert.True(allowDecision.Allowed, allowDecision.DenyReason);
    }

    // ── nested AND/OR condition groups ──

    [Fact]
    public async Task AuthorizeAsync_AnyGroup_DeniesWhenEitherBranchMatches()
    {
        await using AuthorizationDbContext dbContext = await CreateSeededDbContextAsync();
        // DENY when vendorRisk is HIGH OR amount exceeds 100000.
        await AddFinancePolicyAsync(dbContext, "deny-any-group", "DENY", "{\"match\":\"any\",\"conditions\":[{\"attribute\":\"context.vendorRisk\",\"operator\":\"eq\",\"value\":\"HIGH\"},{\"attribute\":\"context.amount\",\"operator\":\"gt\",\"value\":\"100000\"}]}");
        var engine = new EfAuthorizationPolicyEngine(dbContext, new StaticTimeProvider(Now));

        // Only the amount branch matches → OR group matches → DENY fires.
        AuthorizeDecision denyDecision = await engine.AuthorizeAsync(FinanceRequest(new Dictionary<string, object?>
        {
            ["amount"] = 250000,
            ["vendorRisk"] = "LOW",
        }));

        // Neither branch matches → OR group does not match → allowed.
        AuthorizeDecision allowDecision = await engine.AuthorizeAsync(FinanceRequest(new Dictionary<string, object?>
        {
            ["amount"] = 5000,
            ["vendorRisk"] = "LOW",
        }));

        Assert.False(denyDecision.Allowed);
        Assert.Equal("EXPLICIT_DENY", denyDecision.DenyReason);
        Assert.Contains("deny-any-group", denyDecision.MatchedPolicies);
        Assert.True(allowDecision.Allowed, allowDecision.DenyReason);
    }

    [Fact]
    public async Task AuthorizeAsync_NestedGroup_DeniesWhenNestedAnyBranchMatchesWithinAll()
    {
        await using AuthorizationDbContext dbContext = await CreateSeededDbContextAsync();
        // DENY when amount > 1000 AND (vendorRisk is HIGH OR region is EU).
        await AddFinancePolicyAsync(dbContext, "deny-nested-group", "DENY", "{\"match\":\"all\",\"conditions\":[{\"attribute\":\"context.amount\",\"operator\":\"gt\",\"value\":\"1000\"},{\"match\":\"any\",\"conditions\":[{\"attribute\":\"context.vendorRisk\",\"operator\":\"eq\",\"value\":\"HIGH\"},{\"attribute\":\"context.region\",\"operator\":\"eq\",\"value\":\"EU\"}]}]}");
        var engine = new EfAuthorizationPolicyEngine(dbContext, new StaticTimeProvider(Now));

        // amount>1000 AND region==EU → nested OR matches → DENY fires.
        AuthorizeDecision denyDecision = await engine.AuthorizeAsync(FinanceRequest(new Dictionary<string, object?>
        {
            ["amount"] = 5000,
            ["vendorRisk"] = "LOW",
            ["region"] = "EU",
        }));

        // amount>1000 but neither nested branch matches → outer AND fails → allowed.
        AuthorizeDecision allowDecision = await engine.AuthorizeAsync(FinanceRequest(new Dictionary<string, object?>
        {
            ["amount"] = 5000,
            ["vendorRisk"] = "LOW",
            ["region"] = "US",
        }));

        Assert.False(denyDecision.Allowed);
        Assert.Equal("EXPLICIT_DENY", denyDecision.DenyReason);
        Assert.Contains("deny-nested-group", denyDecision.MatchedPolicies);
        Assert.True(allowDecision.Allowed, allowDecision.DenyReason);
    }
}
