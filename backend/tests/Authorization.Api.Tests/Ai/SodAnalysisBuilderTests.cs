using System.Text.Json;
using Authorization.Api.Ai;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Tests.Ai;

/// <summary>
/// Unit tests for <see cref="SodAnalysisBuilder"/>. Detection is entirely deterministic: for each
/// active SoD rule the builder finds every role whose PUBLISHED grants satisfy both matchers and every
/// subject whose combined active assignments satisfy both. These tests seed a compact pricing-like
/// model and assert both role-level and cross-role subject-level detection, plus the matcher semantics.
/// </summary>
public sealed class SodAnalysisBuilderTests
{
    private const string ApplicationId = "pricing-management";
    private readonly Guid appId = Guid.NewGuid();

    [Fact]
    public async Task NoRules_ReturnsEmpty()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync(withRule: false);
        IReadOnlyList<SodViolation> violations = await Build(db);

        Assert.Empty(violations);
    }

    [Fact]
    public async Task RoleHoldingBothSides_ProducesRoleViolation()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync(withRule: true);
        IReadOnlyList<SodViolation> violations = await Build(db);

        // pricing-admin grants both price.submit and price.publish directly.
        SodViolation roleViolation = Assert.Single(violations, v => v.Scope == "ROLE" && v.SubjectKey == "pricing-admin");
        Assert.Equal("segregate-submit-publish", roleViolation.RuleKey);
        Assert.Equal("HIGH", roleViolation.Severity);
        Assert.Equal("role", roleViolation.DeepLinkKind);
        Assert.Contains("price.submit", roleViolation.ConflictingPermissions);
        Assert.Contains("price.publish", roleViolation.ConflictingPermissions);
    }

    [Fact]
    public async Task SubjectSpanningTwoRoles_ProducesSubjectViolation()
    {
        // user9 holds pricing-analyst (submit) AND pricing-lead (publish) via two separate assignments.
        await using AuthorizationDbContext db = await SeedPricingAsync(withRule: true, withCrossRoleSubject: true);
        IReadOnlyList<SodViolation> violations = await Build(db);

        SodViolation subjectViolation = Assert.Single(
            violations,
            v => v.Scope == "SUBJECT" && v.SubjectKey == "user9.mixed@icis.com");
        Assert.Equal("user", subjectViolation.DeepLinkKind);
        Assert.Contains("pricing-analyst", subjectViolation.Detail);
        Assert.Contains("pricing-lead", subjectViolation.Detail);
    }

    [Fact]
    public async Task SingleRoleSubject_DoesNotDoubleReportAsSubject()
    {
        // user7 holds only pricing-lead (publish, no submit) — no violation of the submit/publish rule.
        await using AuthorizationDbContext db = await SeedPricingAsync(withRule: true);
        IReadOnlyList<SodViolation> violations = await Build(db);

        Assert.DoesNotContain(violations, v => v.Scope == "SUBJECT" && v.SubjectKey == "user7.lead@icis.com");
    }

    [Fact]
    public async Task InactiveRule_IsIgnored()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync(withRule: true);
        SodRuleEntity rule = await db.SodRules.SingleAsync();
        rule.Status = "ARCHIVED";
        await db.SaveChangesAsync();

        IReadOnlyList<SodViolation> violations = await Build(db);
        Assert.Empty(violations);
    }

    [Fact]
    public async Task MalformedMatcher_IsSkipped_NotThrown()
    {
        await using AuthorizationDbContext db = await SeedPricingAsync(withRule: true);
        SodRuleEntity rule = await db.SodRules.SingleAsync();
        rule.MatcherA = "not-json";
        await db.SaveChangesAsync();

        IReadOnlyList<SodViolation> violations = await Build(db);
        Assert.Empty(violations);
    }

    [Fact]
    public void SodMatcher_Parse_RejectsEmptyMatcher()
    {
        Assert.Null(SodMatcher.Parse("{}"));
        Assert.Null(SodMatcher.Parse(""));
        Assert.Null(SodMatcher.Parse("[]"));
    }

    [Fact]
    public void SodMatcher_Matches_ByPermissionKey_IsCaseInsensitive()
    {
        SodMatcher? matcher = SodMatcher.Parse("{\"permissionKey\":\"price.publish\"}");
        Assert.NotNull(matcher);
        Assert.True(matcher!.Matches(new PermissionEntity { PermissionKey = "PRICE.PUBLISH", Resource = "price", Action = "publish" }));
        Assert.False(matcher.Matches(new PermissionEntity { PermissionKey = "price.submit", Resource = "price", Action = "submit" }));
    }

    private Task<IReadOnlyList<SodViolation>> Build(AuthorizationDbContext db) =>
        new SodAnalysisBuilder(db).BuildViolationsAsync(appId, ApplicationId, CancellationToken.None);

    private AuthorizationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AuthorizationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AuthorizationDbContext(options);
        db.Applications.Add(new ApplicationEntity
        {
            Id = appId,
            ApplicationId = ApplicationId,
            Name = "Pricing Management",
            TenantRefId = Guid.NewGuid(),
            Status = "ACTIVE",
        });
        db.SaveChanges();
        return db;
    }

    private async Task<AuthorizationDbContext> SeedPricingAsync(bool withRule, bool withCrossRoleSubject = false)
    {
        AuthorizationDbContext db = CreateContext();

        Guid analyst = Guid.NewGuid();
        Guid editor = Guid.NewGuid();
        Guid lead = Guid.NewGuid();
        Guid admin = Guid.NewGuid();
        db.Roles.AddRange(
            Role(analyst, "pricing-analyst", privileged: false),
            Role(editor, "pricing-editor", privileged: false),
            Role(lead, "pricing-lead", privileged: true),
            Role(admin, "pricing-admin", privileged: true));

        Guid view = Guid.NewGuid();
        Guid submit = Guid.NewGuid();
        Guid review = Guid.NewGuid();
        Guid publish = Guid.NewGuid();
        db.Permissions.AddRange(
            Permission(view, "price.view", "price", "view"),
            Permission(submit, "price.submit", "price", "submit"),
            Permission(review, "price.review", "price", "review"),
            Permission(publish, "price.publish", "price", "publish"));

        db.RolePermissions.AddRange(
            RolePermission(analyst, view),
            RolePermission(analyst, submit),
            RolePermission(editor, view),
            RolePermission(editor, review),
            RolePermission(lead, view),
            RolePermission(lead, publish),
            RolePermission(admin, view),
            RolePermission(admin, submit),
            RolePermission(admin, review),
            RolePermission(admin, publish));

        db.Assignments.Add(ActiveAssignment(lead, "user7.lead@icis.com"));
        db.Assignments.Add(RevokedAssignment(admin, "user8.admin@icis.com"));
        if (withCrossRoleSubject)
        {
            db.Assignments.Add(ActiveAssignment(analyst, "user9.mixed@icis.com"));
            db.Assignments.Add(ActiveAssignment(lead, "user9.mixed@icis.com"));
        }

        if (withRule)
        {
            db.SodRules.Add(new SodRuleEntity
            {
                Id = Guid.NewGuid(),
                ApplicationRefId = appId,
                RuleKey = "segregate-submit-publish",
                Name = "Segregate price submission and publishing",
                Rationale = "No single role or user should both submit and publish a price.",
                Severity = "HIGH",
                MatcherA = JsonSerializer.Serialize(new { action = "submit" }),
                MatcherB = JsonSerializer.Serialize(new { action = "publish" }),
                Status = "ACTIVE",
            });
        }

        await db.SaveChangesAsync();
        return db;
    }

    private RoleEntity Role(Guid id, string key, bool privileged) => new()
    {
        Id = id,
        ApplicationRefId = appId,
        RoleKey = key,
        Name = key,
        Privileged = privileged,
        RiskLevel = privileged ? "HIGH" : "MEDIUM",
        Status = "ACTIVE",
    };

    private PermissionEntity Permission(Guid id, string key, string resource, string action) => new()
    {
        Id = id,
        ApplicationRefId = appId,
        PermissionKey = key,
        Resource = resource,
        Action = action,
        RiskLevel = "MEDIUM",
        Status = "ACTIVE",
    };

    private RolePermissionEntity RolePermission(Guid roleId, Guid permissionId) => new()
    {
        Id = Guid.NewGuid(),
        ApplicationRefId = appId,
        RoleRefId = roleId,
        PermissionRefId = permissionId,
        State = "PUBLISHED",
    };

    private AssignmentEntity ActiveAssignment(Guid roleId, string email) => new()
    {
        Id = Guid.NewGuid(),
        ApplicationRefId = appId,
        SubjectType = "USER",
        SubjectEmail = email,
        RoleRefId = roleId,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-10),
        State = "ACTIVE",
    };

    private AssignmentEntity RevokedAssignment(Guid roleId, string email) => new()
    {
        Id = Guid.NewGuid(),
        ApplicationRefId = appId,
        SubjectType = "USER",
        SubjectEmail = email,
        RoleRefId = roleId,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-10),
        State = "REVOKED",
        RevokedAt = DateTimeOffset.UtcNow.AddDays(-1),
    };
}
