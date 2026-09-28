using Authorization.Api.Ai;
using Authorization.Api.Authorization;
using Authorization.Api.Constants;
using Authorization.Api.Governance;
using Authorization.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Authorization.Api.Controllers;

/// <summary>
/// Deterministic governance insight surfaces: configuration advisor findings and
/// separation-of-duties rules/violations. Every result here is computed purely from the
/// structured access model (roles, published grants, active assignments, policies) — no model or
/// AI provider is involved — so these endpoints are available whenever the caller can view the
/// application, <em>independent of whether the AI subsystem is enabled</em>. The optional AI
/// controller layers narration/ranking on top of the very same builders; turning AI off must never
/// hide this baseline signal.
/// </summary>
[ApiController]
[Authorize(Policy = DelegatedAdminPolicyNames.AdminApi)]
[Route("v1/admin/applications/{applicationId}/insights")]
public sealed class ApplicationInsightsController : GovernanceControllerBase
{
    private readonly DelegatedAdminAuthorizationService authorizationService;
    private readonly ConfigAdvisorBuilder configAdvisorBuilder;
    private readonly SodAnalysisBuilder sodAnalysisBuilder;

    public ApplicationInsightsController(
        AuthorizationDbContext dbContext,
        DelegatedAdminAuthorizationService authorizationService,
        ConfigAdvisorBuilder configAdvisorBuilder,
        SodAnalysisBuilder sodAnalysisBuilder)
        : base(dbContext)
    {
        this.authorizationService = authorizationService;
        this.configAdvisorBuilder = configAdvisorBuilder;
        this.sodAnalysisBuilder = sodAnalysisBuilder;
    }

    /// <summary>Deterministic configuration "smells" (stale drafts, duplicate roles, unused permissions, dead context).</summary>
    [HttpGet("config-findings")]
    public async Task<ActionResult<ConfigFindingsResponse>> GetConfigFindingsAsync(
        string applicationId,
        CancellationToken cancellationToken)
    {
        if (!CanView(applicationId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, Error(GovernanceErrorCodes.Forbidden, "You are not permitted to view this application."));
        }

        Guid? appRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (appRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "The application does not exist."));
        }

        IReadOnlyList<ConfigFinding> findings = await configAdvisorBuilder.BuildAsync(
            appRefId.Value,
            applicationId,
            cancellationToken);

        return Ok(new ConfigFindingsResponse(findings.Select(ToConfigFindingResponse).ToList()));
    }

    /// <summary>Active separation-of-duties rules configured for the application.</summary>
    [HttpGet("sod-rules")]
    public async Task<ActionResult<SodRulesResponse>> GetSodRulesAsync(
        string applicationId,
        CancellationToken cancellationToken)
    {
        if (!CanView(applicationId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, Error(GovernanceErrorCodes.Forbidden, "You are not permitted to view this application."));
        }

        Guid? appRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (appRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "The application does not exist."));
        }

        List<SodRuleEntity> rules = await DbContext.SodRules.AsNoTracking()
            .Where(rule => rule.ApplicationRefId == appRefId.Value && rule.Status == "ACTIVE")
            .OrderBy(rule => rule.RuleKey)
            .ToListAsync(cancellationToken);

        return Ok(new SodRulesResponse(rules.Select(ToSodRuleResponse).ToList()));
    }

    /// <summary>Deterministic scan of the access model for subjects/roles that hold both sides of a rule.</summary>
    [HttpGet("sod-violations")]
    public async Task<ActionResult<SodViolationsResponse>> GetSodViolationsAsync(
        string applicationId,
        CancellationToken cancellationToken)
    {
        if (!CanView(applicationId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, Error(GovernanceErrorCodes.Forbidden, "You are not permitted to view this application."));
        }

        Guid? appRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (appRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "The application does not exist."));
        }

        IReadOnlyList<SodViolation> violations = await sodAnalysisBuilder.BuildViolationsAsync(
            appRefId.Value,
            applicationId,
            cancellationToken);

        return Ok(new SodViolationsResponse(violations.Select(ToSodViolationResponse).ToList()));
    }

    private bool CanView(string applicationId) =>
        authorizationService.CanAccessAllApplications(User)
        || authorizationService.IsAuthorized(User, applicationId, DelegatedAdminCapability.ReadOnlyView);

    private static ConfigFindingResponse ToConfigFindingResponse(ConfigFinding finding) =>
        new(
            finding.Id,
            finding.Kind,
            finding.Severity,
            finding.Title,
            finding.Detail,
            finding.EntityType,
            finding.EntityKey,
            SuggestedFix: null);

    private static SodRuleResponse ToSodRuleResponse(SodRuleEntity rule) =>
        new(
            rule.RuleKey,
            rule.Name,
            rule.Rationale,
            rule.Severity,
            ParseMatcherResponse(rule.MatcherA),
            ParseMatcherResponse(rule.MatcherB),
            rule.Status);

    private static SodMatcherResponse ParseMatcherResponse(string json)
    {
        SodMatcher? matcher = SodMatcher.Parse(json);
        return matcher is null
            ? new SodMatcherResponse(null, null, null)
            : new SodMatcherResponse(matcher.PermissionKey, matcher.Resource, matcher.Action);
    }

    private static SodViolationResponse ToSodViolationResponse(SodViolation violation) =>
        new(
            violation.RuleKey,
            violation.RuleName,
            violation.Severity,
            violation.Rationale,
            violation.Scope,
            violation.SubjectKey,
            violation.SubjectLabel,
            violation.ConflictingPermissions,
            violation.Detail,
            violation.DeepLinkKind,
            violation.DeepLinkKey);
}
