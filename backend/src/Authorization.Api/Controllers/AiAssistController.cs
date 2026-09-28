using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Authorization.Ai;
using Authorization.Api.Ai;
using Authorization.Api.Authorization;
using Authorization.Api.Constants;
using Authorization.Api.Governance;
using Authorization.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Authorization.Api.Controllers;

/// <summary>
/// Opt-in AI assistance surfaces. Every action here is advisory and human-in-the-loop:
/// nothing produced by the model is persisted or used in the authorization enforcement
/// path. When AI is disabled (master switch off, provider misconfigured, or the specific
/// feature off) the endpoints respond <c>404 Not Found</c> so the surface is effectively absent.
/// </summary>
[ApiController]
[Authorize(Policy = DelegatedAdminPolicyNames.AdminApi)]
[Route("v1/admin/applications/{applicationId}/ai")]
public sealed class AiAssistController : GovernanceControllerBase
{
    private readonly AiAvailability availability;
    private readonly DelegatedAdminAuthorizationService authorizationService;
    private readonly AiOptions options;
    private readonly IAiAssistant? assistant;
    private readonly DecisionDiagnosticsBuilder diagnosticsBuilder;
    private readonly ImpactAnalysisBuilder impactAnalysisBuilder;
    private readonly ConfigAdvisorBuilder configAdvisorBuilder;
    private readonly AccessSearchExecutor accessSearchExecutor;
    private readonly SodAnalysisBuilder sodAnalysisBuilder;
    private readonly AiInvocationRecorder invocationRecorder;
    private readonly AiPromptLogRecorder promptLogRecorder;

    public AiAssistController(
        AuthorizationDbContext dbContext,
        AiAvailability availability,
        DelegatedAdminAuthorizationService authorizationService,
        IOptions<AiOptions> options,
        DecisionDiagnosticsBuilder diagnosticsBuilder,
        ImpactAnalysisBuilder impactAnalysisBuilder,
        ConfigAdvisorBuilder configAdvisorBuilder,
        AccessSearchExecutor accessSearchExecutor,
        SodAnalysisBuilder sodAnalysisBuilder,
        AiInvocationRecorder invocationRecorder,
        AiPromptLogRecorder promptLogRecorder,
        IServiceProvider serviceProvider)
        : base(dbContext)
    {
        this.availability = availability;
        this.authorizationService = authorizationService;
        this.options = options.Value;
        this.diagnosticsBuilder = diagnosticsBuilder;
        this.impactAnalysisBuilder = impactAnalysisBuilder;
        this.configAdvisorBuilder = configAdvisorBuilder;
        this.accessSearchExecutor = accessSearchExecutor;
        this.sodAnalysisBuilder = sodAnalysisBuilder;
        this.invocationRecorder = invocationRecorder;
        this.promptLogRecorder = promptLogRecorder;

        // Resolved via the container so a disabled AI subsystem (no registration) yields null
        // rather than a construction failure.
        assistant = serviceProvider.GetService(typeof(IAiAssistant)) as IAiAssistant;
    }

    [HttpPost("policy-draft")]
    public async Task<ActionResult<PolicyDraftResponse>> DraftPolicyAsync(
        string applicationId,
        [FromBody] PolicyDraftRequestBody body,
        CancellationToken cancellationToken)
    {
        if (!availability.Enabled || !availability.Features.PolicyAuthoring || assistant is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "AI policy authoring is not enabled."));
        }

        if (!authorizationService.CanAccessAllApplications(User)
            && !authorizationService.IsAuthorized(User, applicationId, DelegatedAdminCapability.ManagePolicies))
        {
            return StatusCode(StatusCodes.Status403Forbidden, Error(GovernanceErrorCodes.Forbidden, "You are not permitted to author policies for this application."));
        }

        string instruction = (body.Instruction ?? string.Empty).Trim();
        if (instruction.Length == 0)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "An instruction is required."));
        }

        if (instruction.Length > options.Limits.MaxPromptChars)
        {
            await promptLogRecorder.RecordAsync(
                PromptContext("policyAuthoring", applicationId, instruction),
                AiPromptOutcomes.InvalidInput,
                $"The instruction exceeds the maximum of {options.Limits.MaxPromptChars} characters.");
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, $"The instruction exceeds the maximum of {options.Limits.MaxPromptChars} characters."));
        }

        Guid? appRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (appRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "The application does not exist."));
        }

        List<string> permissionKeys = await DbContext.Permissions
            .AsNoTracking()
            .Where(permission => permission.ApplicationRefId == appRefId)
            .Select(permission => permission.PermissionKey)
            .ToListAsync(cancellationToken);

        var request = new PolicyDraftRequest(
            ApplicationId: applicationId,
            Instruction: instruction,
            KnownPermissionKeys: permissionKeys,
            KnownAttributes: []);

        string outcome = AiPromptOutcomes.Succeeded;
        string? errorMessage = null;
        PolicyDraftResult? result = null;
        try
        {
            result = await InvokeWithTimeoutAsync(
                "policyAuthoring",
                applicationId,
                token => assistant.DraftPolicyAsync(request, token),
                cancellationToken);

            // Defence in depth: never hand the client output that is not valid JSON.
            if (!IsValidJson(result.ConditionsJson))
            {
                outcome = AiPromptOutcomes.ModelError;
                errorMessage = "The assistant returned an unusable (non-JSON) draft.";
                return StatusCode(StatusCodes.Status502BadGateway, Error(GovernanceErrorCodes.ValidationError, "The assistant returned an unusable draft. Please try again."));
            }

            return Ok(new PolicyDraftResponse(result.ConditionsJson, result.Summary, result.Warnings, result.SuggestedEffect));
        }
        catch (OperationCanceledException)
        {
            outcome = AiPromptOutcomes.Timeout;
            errorMessage = "The AI request timed out.";
            throw;
        }
        catch (Exception ex)
        {
            outcome = AiPromptOutcomes.ModelError;
            errorMessage = ex.Message;
            throw;
        }
        finally
        {
            await promptLogRecorder.RecordAsync(
                PromptContext("policyAuthoring", applicationId, instruction),
                outcome,
                errorMessage,
                result is null ? null : new { result.ConditionsJson, result.Summary, result.SuggestedEffect, result.Warnings });
        }
    }

    [HttpPost("explain-decision")]
    public async Task<ActionResult<DecisionExplanationResponse>> ExplainDecisionAsync(
        string applicationId,
        [FromBody] ExplainDecisionRequestBody body,
        CancellationToken cancellationToken)
    {
        if (!availability.Enabled || !availability.Features.DecisionExplainer || assistant is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "AI decision explanations are not enabled."));
        }

        if (!authorizationService.CanAccessAllApplications(User)
            && !authorizationService.IsAuthorized(User, applicationId, DelegatedAdminCapability.ReadOnlyView))
        {
            return StatusCode(StatusCodes.Status403Forbidden, Error(GovernanceErrorCodes.Forbidden, "You are not permitted to view this application."));
        }

        Guid? appRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (appRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "The application does not exist."));
        }

        string subjectType = string.IsNullOrWhiteSpace(body.SubjectType) ? "USER" : body.SubjectType;
        string resourceType = body.ResourceType ?? string.Empty;
        string action = body.Action ?? string.Empty;
        IReadOnlyList<string> matchedPolicies = body.MatchedPolicies ?? [];

        // Diagnostics ground the explanation in real store data so the model can pinpoint the exact
        // cause of a denial. Only gathered for denials — an allow needs no root-cause analysis.
        DecisionDiagnostics? diagnostics = null;
        if (!body.Allowed)
        {
            diagnostics = await diagnosticsBuilder.BuildAsync(
                appRefId.Value,
                subjectType,
                string.IsNullOrWhiteSpace(body.SubjectEmail) ? null : body.SubjectEmail,
                resourceType,
                action,
                matchedPolicies,
                body.ContextKeys ?? [],
                cancellationToken);
        }

        var request = new DecisionExplanationRequest(
            ApplicationId: applicationId,
            Allowed: body.Allowed,
            DenyReason: body.DenyReason,
            SubjectType: subjectType,
            SubjectEmail: string.IsNullOrWhiteSpace(body.SubjectEmail) ? null : body.SubjectEmail,
            ResourceType: resourceType,
            ResourceId: body.ResourceId,
            Action: action,
            MatchedRoles: body.MatchedRoles ?? [],
            MatchedPermissions: body.MatchedPermissions ?? [],
            MatchedPolicies: matchedPolicies,
            Diagnostics: diagnostics);

        DecisionExplanationResult result = await InvokeWithTimeoutAsync(
            "decisionExplainer",
            applicationId,
            token => assistant.ExplainDecisionAsync(request, token),
            cancellationToken);

        return Ok(new DecisionExplanationResponse(result.Narrative, result.Remediation));
    }

    [HttpPost("impact-analysis")]
    public async Task<ActionResult<ImpactAnalysisResponse>> AnalyzeImpactAsync(
        string applicationId,
        [FromBody] ImpactAnalysisRequestBody body,
        CancellationToken cancellationToken)
    {
        if (!availability.Enabled || !availability.Features.ImpactAnalysis || assistant is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "AI impact analysis is not enabled."));
        }

        if (!authorizationService.CanAccessAllApplications(User)
            && !authorizationService.IsAuthorized(User, applicationId, DelegatedAdminCapability.ManagePolicies))
        {
            return StatusCode(StatusCodes.Status403Forbidden, Error(GovernanceErrorCodes.Forbidden, "You are not permitted to analyze policies for this application."));
        }

        string policyKey = (body.PolicyKey ?? string.Empty).Trim();
        if (policyKey.Length == 0)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "A policyKey is required."));
        }

        Guid? appRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (appRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "The application does not exist."));
        }

        ImpactAnalysisFacts? facts = await impactAnalysisBuilder.BuildAsync(
            appRefId.Value,
            applicationId,
            policyKey,
            cancellationToken);
        if (facts is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "No draft policy with that key exists for this application."));
        }

        var narrationRequest = new ImpactNarrationRequest(
            ApplicationId: applicationId,
            PolicyKey: facts.PolicyKey,
            Effect: facts.Effect,
            EvaluatedCount: facts.EvaluatedCount,
            AllowToDenyCount: facts.AllowToDenyCount,
            DenyToAllowCount: facts.DenyToAllowCount,
            SampledFromHistory: facts.SampledFromHistory,
            Flips: facts.Flips);

        ImpactNarrationResult narration = await InvokeWithTimeoutAsync(
            "impactAnalysis",
            applicationId,
            token => assistant.NarrateImpactAsync(narrationRequest, token),
            cancellationToken);

        var flips = facts.Flips
            .Select(flip => new ImpactFlipResponse(
                flip.SubjectEmail,
                flip.ResourceId,
                flip.Action,
                flip.Before,
                flip.After,
                flip.Reason))
            .ToList();

        return Ok(new ImpactAnalysisResponse(
            PolicyKey: facts.PolicyKey,
            Effect: facts.Effect,
            EvaluatedCount: facts.EvaluatedCount,
            AllowToDenyCount: facts.AllowToDenyCount,
            DenyToAllowCount: facts.DenyToAllowCount,
            SampledFromHistory: facts.SampledFromHistory,
            Summary: narration.Summary,
            Flips: flips));
    }

    [HttpGet("advisor/findings")]
    public async Task<ActionResult<ConfigFindingsResponse>> GetAdvisorFindingsAsync(
        string applicationId,
        CancellationToken cancellationToken)
    {
        // The raw findings are fully deterministic and do not need the model, so this endpoint is
        // available whenever the Config Advisor feature is on — the AI-off experience is exactly
        // this unranked list.
        if (!availability.Enabled || !availability.Features.ConfigAdvisor)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "The configuration advisor is not enabled."));
        }

        if (!authorizationService.CanAccessAllApplications(User)
            && !authorizationService.IsAuthorized(User, applicationId, DelegatedAdminCapability.ReadOnlyView))
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

        return Ok(new ConfigFindingsResponse(
            findings.Select(finding => ToResponse(finding, suggestedFix: null)).ToList()));
    }

    [HttpPost("advisor/summarize")]
    public async Task<ActionResult<ConfigAdvisorSummaryResponse>> SummarizeAdvisorFindingsAsync(
        string applicationId,
        CancellationToken cancellationToken)
    {
        if (!availability.Enabled || !availability.Features.ConfigAdvisor || assistant is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "The configuration advisor is not enabled."));
        }

        if (!authorizationService.CanAccessAllApplications(User)
            && !authorizationService.IsAuthorized(User, applicationId, DelegatedAdminCapability.ReadOnlyView))
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

        var advisorRequest = new ConfigAdvisorRequest(
            ApplicationId: applicationId,
            Findings: findings
                .Select(finding => new ConfigFindingFact(
                    finding.Id,
                    finding.Kind,
                    finding.Severity,
                    finding.Title,
                    finding.Detail,
                    finding.EntityType,
                    finding.EntityKey))
                .ToList());

        ConfigAdvisorResult result = await InvokeWithTimeoutAsync(
            "configAdvisor",
            applicationId,
            token => assistant.SummarizeFindingsAsync(advisorRequest, token),
            cancellationToken);

        var fixesById = result.Suggestions
            .GroupBy(suggestion => suggestion.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().SuggestedFix, StringComparer.OrdinalIgnoreCase);

        List<ConfigFindingResponse> responseFindings = findings
            .Select(finding => ToResponse(
                finding,
                fixesById.TryGetValue(finding.Id, out string? fix) ? fix : null))
            .ToList();

        return Ok(new ConfigAdvisorSummaryResponse(result.Summary, responseFindings));
    }

    private static ConfigFindingResponse ToResponse(ConfigFinding finding, string? suggestedFix) =>
        new(
            finding.Id,
            finding.Kind,
            finding.Severity,
            finding.Title,
            finding.Detail,
            finding.EntityType,
            finding.EntityKey,
            suggestedFix);

    [HttpPost("access-search")]
    public async Task<ActionResult<AccessSearchResponse>> AccessSearchAsync(
        string applicationId,
        [FromBody] AccessSearchRequestBody body,
        CancellationToken cancellationToken)
    {
        if (!availability.Enabled || !availability.Features.AccessSearch || assistant is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "Natural-language access search is not enabled."));
        }

        if (!authorizationService.CanAccessAllApplications(User)
            && !authorizationService.IsAuthorized(User, applicationId, DelegatedAdminCapability.ReadOnlyView))
        {
            return StatusCode(StatusCodes.Status403Forbidden, Error(GovernanceErrorCodes.Forbidden, "You are not permitted to view this application."));
        }

        string question = (body.Question ?? string.Empty).Trim();
        if (question.Length == 0)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "A question is required."));
        }

        if (question.Length > options.Limits.MaxPromptChars)
        {
            await promptLogRecorder.RecordAsync(
                PromptContext("accessSearch", applicationId, question),
                AiPromptOutcomes.InvalidInput,
                $"The question exceeds the maximum of {options.Limits.MaxPromptChars} characters.");
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, $"The question exceeds the maximum of {options.Limits.MaxPromptChars} characters."));
        }

        Guid? appRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (appRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "The application does not exist."));
        }

        // Ground the model in the app's real vocabulary and the closed schema so it can only ever
        // produce a spec over fields and values that exist. The vocabulary is relevance-scoped to the
        // question so the request stays bounded as the catalog grows.
        AccessSearchVocabulary vocabulary = await accessSearchExecutor.BuildRelevantVocabularyAsync(
            [appRefId.Value],
            question,
            cancellationToken);

        var planRequest = new AccessSearchPlanRequest(
            question,
            AccessSearchExecutor.BuildPlannerSchema(),
            vocabulary,
            AccessSearchExecutor.HierarchyOverview);

        string logOutcome = AiPromptOutcomes.Succeeded;
        string? logError = null;
        AccessSearchPlanResult? plannedForLog = null;
        try
        {
            AccessSearchPlanResult plan = await InvokeWithTimeoutAsync(
                "accessSearch",
                applicationId,
                token => assistant.PlanAccessSearchAsync(planRequest, token),
                cancellationToken);
            plannedForLog = plan;

            if (string.IsNullOrWhiteSpace(plan.Entity))
            {
                logOutcome = AiPromptOutcomes.Unmapped;
                logError = "The question could not be mapped to a valid access search.";
                return Ok(BuildAccessSearchGuidance(
                    question,
                    vocabulary,
                    "I couldn't tell which part of the access model that question was about.",
                    plan.Suggestions));
            }

            var spec = new AccessSearchSpec(
                plan.Entity,
                plan.Filters.Select(f => new AccessSearchFilter(f.Field, f.Operator, f.Value)).ToList(),
                plan.Aggregate,
                plan.GroupBy,
                plan.Include,
                plan.GroupByField,
                plan.Limit,
                plan.AbsentRelationship,
                plan.IncludeChild,
                plan.PresentRelationship,
                plan.GroupBySecondaryField,
                plan.IncludeGrandchild);

            // Resolve every filter value against the live data before executing so a mis-cased or
            // invented value becomes an unambiguous real value — or a helpful "did you mean" guidance —
            // rather than misleading or empty results. Correctness never depends on the prompt.
            AccessSearchValueResolution resolution = await accessSearchExecutor.ResolveFilterValuesAsync(
                [appRefId.Value],
                spec,
                cancellationToken);
            if (!resolution.Success)
            {
                logOutcome = AiPromptOutcomes.ValidationFailed;
                logError = resolution.Error;
                return Ok(BuildAccessSearchGuidance(question, vocabulary, resolution.Error!, plan.Suggestions));
            }

            spec = resolution.Spec!;

            AccessSearchOutcome outcome = await accessSearchExecutor.ExecuteAsync(
                appRefId.Value,
                applicationId,
                spec,
                cancellationToken);

            if (!outcome.IsValid)
            {
                logOutcome = AiPromptOutcomes.ValidationFailed;
                logError = outcome.Error ?? "The search could not be executed.";
                return Ok(BuildAccessSearchGuidance(
                    question,
                    vocabulary,
                    "That question referenced a field or filter I don't support yet.",
                    plan.Suggestions));
            }

            return Ok(ToAccessSearchResponse(plan, spec, outcome));
        }
        catch (OperationCanceledException)
        {
            logOutcome = AiPromptOutcomes.Timeout;
            logError = "The AI request timed out.";
            throw;
        }
        catch (Exception ex)
        {
            logOutcome = AiPromptOutcomes.ModelError;
            logError = ex.Message;
            throw;
        }
        finally
        {
            await promptLogRecorder.RecordAsync(
                PromptContext("accessSearch", applicationId, question),
                logOutcome,
                logError,
                plannedForLog is null ? null : new { plannedForLog.Entity, plannedForLog.Explanation, plannedForLog.Filters });
        }
    }

    private static AccessSearchResponse ToAccessSearchResponse(AccessSearchPlanResult plan, AccessSearchSpec spec, AccessSearchOutcome outcome) =>
        new(
            outcome.Entity,
            plan.Explanation,
            spec.Filters.Select(f => new AccessSearchFilterResponse(f.Field, f.Operator, f.Value)).ToList(),
            outcome.Rows.Select(MapAccessSearchRow).ToList(),
            outcome.Mode);

    /// <summary>
    /// Builds a friendly, capability-aware guidance response for a question the engine could not
    /// answer. Shared by the per-application and platform controllers so both surface the same
    /// proactive suggestions instead of a bare error.
    /// </summary>
    internal static AccessSearchResponse BuildAccessSearchGuidance(
        string question,
        AccessSearchVocabulary vocabulary,
        string reason,
        IReadOnlyList<string>? aiSuggestions = null)
    {
        AccessSearchGuidance guidance = AccessSearchSuggester.Build(question, vocabulary, reason, aiSuggestions);
        return new AccessSearchResponse(
            string.Empty,
            guidance.Reason,
            [],
            [],
            AccessSearchModes.Guidance,
            new AccessSearchGuidanceResponse(guidance.Reason, guidance.Intents, guidance.Suggestions));
    }

    /// <summary>Maps an executor row to the API response shape, recursing into included children.</summary>
    internal static AccessSearchResultResponse MapAccessSearchRow(AccessSearchRow row) =>
        new(
            row.ApplicationId,
            row.EntityType,
            row.Title,
            row.Detail,
            row.DeepLinkKind,
            row.DeepLinkKey,
            row.Children is { Count: > 0 } children ? children.Select(MapAccessSearchRow).ToList() : null,
            row.ApplicationName,
            row.TenantName);

    [HttpGet("sod/rules")]
    public async Task<ActionResult<SodRulesResponse>> GetSodRulesAsync(
        string applicationId,
        CancellationToken cancellationToken)
    {
        if (!availability.Enabled || !availability.Features.SodAnalysis)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "Separation-of-duties analysis is not enabled."));
        }

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

    [HttpGet("sod/violations")]
    public async Task<ActionResult<SodViolationsResponse>> GetSodViolationsAsync(
        string applicationId,
        CancellationToken cancellationToken)
    {
        // Violations are fully deterministic; available whenever the feature is on (no model needed).
        if (!availability.Enabled || !availability.Features.SodAnalysis)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "Separation-of-duties analysis is not enabled."));
        }

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

    [HttpPost("sod/rules/draft")]
    public async Task<ActionResult<SodRuleDraftResponse>> DraftSodRuleAsync(
        string applicationId,
        [FromBody] SodRuleDraftRequestBody body,
        CancellationToken cancellationToken)
    {
        if (!availability.Enabled || !availability.Features.SodAnalysis || assistant is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "Separation-of-duties analysis is not enabled."));
        }

        if (!CanManage(applicationId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, Error(GovernanceErrorCodes.Forbidden, "You are not permitted to author rules for this application."));
        }

        string instruction = (body.Instruction ?? string.Empty).Trim();
        if (instruction.Length == 0)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "An instruction is required."));
        }

        if (instruction.Length > options.Limits.MaxPromptChars)
        {
            await promptLogRecorder.RecordAsync(
                PromptContext("sodAnalysis", applicationId, instruction),
                AiPromptOutcomes.InvalidInput,
                $"The instruction exceeds the maximum of {options.Limits.MaxPromptChars} characters.");
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, $"The instruction exceeds the maximum of {options.Limits.MaxPromptChars} characters."));
        }

        Guid? appRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (appRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "The application does not exist."));
        }

        // Ground the model in the app's real permission vocabulary so drafted matchers cannot invent
        // permissions, resources, or actions that do not exist.
        AccessSearchVocabulary vocabulary = await accessSearchExecutor.BuildVocabularyAsync(
            [appRefId.Value],
            cancellationToken);

        var draftRequest = new SodRuleDraftRequest(
            applicationId,
            instruction,
            vocabulary.PermissionKeys,
            vocabulary.Resources,
            vocabulary.Actions);

        string outcome = AiPromptOutcomes.Succeeded;
        string? errorMessage = null;
        SodRuleDraftResult? draft = null;
        try
        {
            draft = await InvokeWithTimeoutAsync(
                "sodAnalysis",
                applicationId,
                token => assistant.DraftSodRuleAsync(draftRequest, token),
                cancellationToken);

            return Ok(new SodRuleDraftResponse(
                draft.Name,
                draft.Rationale,
                NormalizeSeverity(draft.Severity),
                ToSodMatcherResponse(draft.MatcherA),
                ToSodMatcherResponse(draft.MatcherB),
                draft.Warnings));
        }
        catch (OperationCanceledException)
        {
            outcome = AiPromptOutcomes.Timeout;
            errorMessage = "The AI request timed out.";
            throw;
        }
        catch (Exception ex)
        {
            outcome = AiPromptOutcomes.ModelError;
            errorMessage = ex.Message;
            throw;
        }
        finally
        {
            await promptLogRecorder.RecordAsync(
                PromptContext("sodAnalysis", applicationId, instruction),
                outcome,
                errorMessage,
                draft is null ? null : new { draft.Name, draft.Rationale, draft.Severity, draft.MatcherA, draft.MatcherB, draft.Warnings });
        }
    }

    [HttpPost("sod/rules")]
    public async Task<ActionResult<SodRuleResponse>> SaveSodRuleAsync(
        string applicationId,
        [FromBody] SodRuleSaveRequestBody body,
        CancellationToken cancellationToken)
    {
        if (!availability.Enabled || !availability.Features.SodAnalysis)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "Separation-of-duties analysis is not enabled."));
        }

        if (!CanManage(applicationId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, Error(GovernanceErrorCodes.Forbidden, "You are not permitted to author rules for this application."));
        }

        string ruleKey = (body.RuleKey ?? string.Empty).Trim().ToLowerInvariant();
        string name = (body.Name ?? string.Empty).Trim();
        if (ruleKey.Length == 0 || name.Length == 0)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "A rule key and name are required."));
        }

        string severity = NormalizeSeverity(body.Severity);

        Guid? appRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (appRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "The application does not exist."));
        }

        // Validate that both matchers reference only permissions/resources/actions that actually exist,
        // so a saved rule (whether AI-drafted or hand-authored) can never reference invented keys.
        AccessSearchVocabulary vocabulary = await accessSearchExecutor.BuildVocabularyAsync(
            [appRefId.Value],
            cancellationToken);

        string? matcherErrorA = ValidateMatcher(body.MatcherA, vocabulary, "matcherA");
        if (matcherErrorA is not null)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, matcherErrorA));
        }

        string? matcherErrorB = ValidateMatcher(body.MatcherB, vocabulary, "matcherB");
        if (matcherErrorB is not null)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, matcherErrorB));
        }

        string matcherAJson = SerializeMatcher(body.MatcherA!);
        string matcherBJson = SerializeMatcher(body.MatcherB!);
        if (string.Equals(matcherAJson, matcherBJson, StringComparison.OrdinalIgnoreCase))
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "The two sides of a rule must reference different permissions."));
        }

        SodRuleEntity? existing = await DbContext.SodRules
            .FirstOrDefaultAsync(rule => rule.ApplicationRefId == appRefId.Value && rule.RuleKey == ruleKey, cancellationToken);

        if (existing is null)
        {
            existing = new SodRuleEntity
            {
                ApplicationRefId = appRefId.Value,
                RuleKey = ruleKey,
                CreatedBy = User.Identity?.Name ?? "system",
            };
            DbContext.SodRules.Add(existing);
        }
        else
        {
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.UpdatedBy = User.Identity?.Name ?? "system";
        }

        existing.Name = name;
        existing.Rationale = string.IsNullOrWhiteSpace(body.Rationale) ? null : body.Rationale!.Trim();
        existing.Severity = severity;
        existing.MatcherA = matcherAJson;
        existing.MatcherB = matcherBJson;
        existing.Status = GovernanceStatus.Active;

        await DbContext.SaveChangesAsync(cancellationToken);

        return Ok(ToSodRuleResponse(existing));
    }

    [HttpDelete("sod/rules/{ruleKey}")]
    public async Task<IActionResult> DeleteSodRuleAsync(
        string applicationId,
        string ruleKey,
        CancellationToken cancellationToken)
    {
        if (!availability.Enabled || !availability.Features.SodAnalysis)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "Separation-of-duties analysis is not enabled."));
        }

        if (!CanManage(applicationId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, Error(GovernanceErrorCodes.Forbidden, "You are not permitted to author rules for this application."));
        }

        string normalizedKey = (ruleKey ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedKey.Length == 0)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "The rule does not exist."));
        }

        Guid? appRefId = await ResolveApplicationRefIdAsync(applicationId, cancellationToken);
        if (appRefId is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ApplicationNotFound, "The application does not exist."));
        }

        SodRuleEntity? existing = await DbContext.SodRules
            .FirstOrDefaultAsync(
                rule => rule.ApplicationRefId == appRefId.Value
                    && rule.RuleKey == normalizedKey
                    && rule.Status == GovernanceStatus.Active,
                cancellationToken);

        if (existing is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "The rule does not exist."));
        }

        // Soft-delete (archive) so the rule drops out of active detection and listings while its
        // authoring history is preserved for audit; re-saving the same rule key reactivates it.
        existing.Status = GovernanceStatus.Archived;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        existing.UpdatedBy = User.Identity?.Name ?? "system";

        await DbContext.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    private bool CanView(string applicationId) =>
        authorizationService.CanAccessAllApplications(User)
        || authorizationService.IsAuthorized(User, applicationId, DelegatedAdminCapability.ReadOnlyView);

    private bool CanManage(string applicationId) =>
        authorizationService.CanAccessAllApplications(User)
        || authorizationService.IsAuthorized(User, applicationId, DelegatedAdminCapability.ManagePolicies);

    private static string NormalizeSeverity(string? severity)
    {
        string value = (severity ?? string.Empty).Trim().ToUpperInvariant();
        return value is "LOW" or "MEDIUM" or "HIGH" or "CRITICAL" ? value : "HIGH";
    }

    // Validates a matcher body: it must set at least one field, and any referenced permission key,
    // resource, or action must exist in the application's real vocabulary.
    private static string? ValidateMatcher(SodMatcherBody? matcher, AccessSearchVocabulary vocabulary, string label)
    {
        if (matcher is null)
        {
            return $"{label} is required.";
        }

        string? permissionKey = Trim(matcher.PermissionKey);
        string? resource = Trim(matcher.Resource);
        string? action = Trim(matcher.Action);

        if (permissionKey is null && resource is null && action is null)
        {
            return $"{label} must reference a permission key, or a resource and/or action.";
        }

        if (permissionKey is not null
            && !vocabulary.PermissionKeys.Contains(permissionKey, StringComparer.OrdinalIgnoreCase))
        {
            return $"{label} references an unknown permission key '{permissionKey}'.";
        }

        if (resource is not null
            && !vocabulary.Resources.Contains(resource, StringComparer.OrdinalIgnoreCase))
        {
            return $"{label} references an unknown resource '{resource}'.";
        }

        if (action is not null
            && !vocabulary.Actions.Contains(action, StringComparer.OrdinalIgnoreCase))
        {
            return $"{label} references an unknown action '{action}'.";
        }

        return null;
    }

    private static string SerializeMatcher(SodMatcherBody matcher)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        string? permissionKey = Trim(matcher.PermissionKey);
        string? resource = Trim(matcher.Resource);
        string? action = Trim(matcher.Action);
        if (permissionKey is not null)
        {
            normalized["permissionKey"] = permissionKey;
        }
        else
        {
            if (resource is not null)
            {
                normalized["resource"] = resource;
            }

            if (action is not null)
            {
                normalized["action"] = action;
            }
        }

        return JsonSerializer.Serialize(normalized);
    }

    private static string? Trim(string? value)
    {
        string trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

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

    private static SodMatcherResponse ToSodMatcherResponse(SodMatcherDraft matcher) =>
        new(Trim(matcher.PermissionKey), Trim(matcher.Resource), Trim(matcher.Action));

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

    private Task<T> InvokeWithTimeoutAsync<T>(
        string feature,
        string? applicationId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var context = new AiInvocationContext(feature, applicationId, Actor, ActorRole, HttpContext.TraceIdentifier);
        return invocationRecorder.InvokeAsync(context, operation, cancellationToken);
    }

    private AiPromptLogContext PromptContext(string feature, string? applicationId, string prompt) =>
        new(feature, applicationId, Actor, ActorRole, prompt, HttpContext.TraceIdentifier);

    private static bool IsValidJson(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed class PolicyDraftRequestBody
{
    [Required]
    public string? Instruction { get; init; }
}

public sealed record PolicyDraftResponse(
    string ConditionsJson,
    string Summary,
    IReadOnlyList<string> Warnings,
    string? SuggestedEffect);

public sealed class ExplainDecisionRequestBody
{
    public bool Allowed { get; init; }
    public string? DenyReason { get; init; }
    public string? SubjectType { get; init; }
    public string? SubjectEmail { get; init; }
    public string? ResourceType { get; init; }
    public string? ResourceId { get; init; }
    public string? Action { get; init; }
    public IReadOnlyList<string>? MatchedRoles { get; init; }
    public IReadOnlyList<string>? MatchedPermissions { get; init; }
    public IReadOnlyList<string>? MatchedPolicies { get; init; }
    public IReadOnlyList<string>? ContextKeys { get; init; }
}

public sealed record DecisionExplanationResponse(
    string Narrative,
    IReadOnlyList<string> Remediation);

public sealed class ImpactAnalysisRequestBody
{
    [Required]
    public string? PolicyKey { get; init; }
}

public sealed record ImpactAnalysisResponse(
    string PolicyKey,
    string Effect,
    int EvaluatedCount,
    int AllowToDenyCount,
    int DenyToAllowCount,
    bool SampledFromHistory,
    string Summary,
    IReadOnlyList<ImpactFlipResponse> Flips);

public sealed record ImpactFlipResponse(
    string? SubjectEmail,
    string? ResourceId,
    string Action,
    bool Before,
    bool After,
    string? Reason);

public sealed record ConfigFindingResponse(
    string Id,
    string Kind,
    string Severity,
    string Title,
    string Detail,
    string? EntityType,
    string? EntityKey,
    string? SuggestedFix);

public sealed record ConfigFindingsResponse(
    IReadOnlyList<ConfigFindingResponse> Findings);

public sealed record ConfigAdvisorSummaryResponse(
    string Summary,
    IReadOnlyList<ConfigFindingResponse> Findings);

public sealed class AccessSearchRequestBody
{
    [Required]
    public string? Question { get; init; }
}

public sealed record AccessSearchResponse(
    string Entity,
    string? Explanation,
    IReadOnlyList<AccessSearchFilterResponse> Filters,
    IReadOnlyList<AccessSearchResultResponse> Results,
    string Mode = "records",
    AccessSearchGuidanceResponse? Guidance = null);

/// <summary>
/// Present when a question could not be confidently answered (<c>Mode == "guidance"</c>): a plain-language
/// reason, the most likely interpretations, and ready-to-run prompts the engine can actually answer.
/// </summary>
public sealed record AccessSearchGuidanceResponse(
    string Reason,
    IReadOnlyList<string> Intents,
    IReadOnlyList<string> Suggestions);

public sealed record AccessSearchFilterResponse(
    string Field,
    string Operator,
    string Value);

public sealed record AccessSearchResultResponse(
    string ApplicationId,
    string EntityType,
    string Title,
    string Detail,
    string? DeepLinkKind,
    string? DeepLinkKey,
    IReadOnlyList<AccessSearchResultResponse>? Children = null,
    string ApplicationName = "",
    string TenantName = "");

public sealed class SodRuleDraftRequestBody
{
    [Required]
    public string? Instruction { get; init; }
}

public sealed class SodRuleSaveRequestBody
{
    [Required]
    public string? RuleKey { get; init; }
    [Required]
    public string? Name { get; init; }
    public string? Rationale { get; init; }
    public string? Severity { get; init; }
    [Required]
    public SodMatcherBody? MatcherA { get; init; }
    [Required]
    public SodMatcherBody? MatcherB { get; init; }
}

public sealed class SodMatcherBody
{
    public string? PermissionKey { get; init; }
    public string? Resource { get; init; }
    public string? Action { get; init; }
}

public sealed record SodMatcherResponse(
    string? PermissionKey,
    string? Resource,
    string? Action);

public sealed record SodRuleResponse(
    string RuleKey,
    string Name,
    string? Rationale,
    string Severity,
    SodMatcherResponse MatcherA,
    SodMatcherResponse MatcherB,
    string Status);

public sealed record SodRulesResponse(
    IReadOnlyList<SodRuleResponse> Rules);

public sealed record SodRuleDraftResponse(
    string Name,
    string Rationale,
    string Severity,
    SodMatcherResponse MatcherA,
    SodMatcherResponse MatcherB,
    IReadOnlyList<string> Warnings);

public sealed record SodViolationResponse(
    string RuleKey,
    string RuleName,
    string Severity,
    string? Rationale,
    string Scope,
    string SubjectKey,
    string SubjectLabel,
    IReadOnlyList<string> ConflictingPermissions,
    string Detail,
    string? DeepLinkKind,
    string? DeepLinkKey);

public sealed record SodViolationsResponse(
    IReadOnlyList<SodViolationResponse> Violations);
