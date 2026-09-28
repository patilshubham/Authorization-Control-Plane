using System.ComponentModel.DataAnnotations;
using Authorization.Ai;
using Authorization.Api.Ai;
using Authorization.Api.Authorization;
using Authorization.Api.Constants;
using Authorization.Api.Contracts;
using Authorization.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace Authorization.Api.Controllers;

/// <summary>
/// Platform-wide (cross-application) AI assistance surfaces. Like <see cref="AiAssistController"/>
/// every action is advisory, read-only, and disappears (<c>404</c>) when AI or the specific
/// feature is off. Results are scoped to the applications the caller may view.
/// </summary>
[ApiController]
[Authorize(Policy = DelegatedAdminPolicyNames.AdminApi)]
[Route("v1/admin/ai")]
public sealed class PlatformAiAssistController : GovernanceControllerBase
{
    // Cap total aggregated rows so a broad question cannot return an unbounded result set.
    private const int MaxTotalResults = 200;

    private readonly AiAvailability availability;
    private readonly DelegatedAdminAuthorizationService authorizationService;
    private readonly AiOptions options;
    private readonly IAiAssistant? assistant;
    private readonly AccessSearchExecutor accessSearchExecutor;
    private readonly AccessReviewBuilder accessReviewBuilder;
    private readonly SubjectPseudonymizer pseudonymizer;
    private readonly AuditNarrativeBuilder auditNarrativeBuilder;
    private readonly AiInvocationRecorder invocationRecorder;
    private readonly AiUsageBuilder usageBuilder;
    private readonly AiPromptLogRecorder promptLogRecorder;

    public PlatformAiAssistController(
        AuthorizationDbContext dbContext,
        AiAvailability availability,
        DelegatedAdminAuthorizationService authorizationService,
        IOptions<AiOptions> options,
        AccessSearchExecutor accessSearchExecutor,
        AccessReviewBuilder accessReviewBuilder,
        SubjectPseudonymizer pseudonymizer,
        AuditNarrativeBuilder auditNarrativeBuilder,
        AiInvocationRecorder invocationRecorder,
        AiUsageBuilder usageBuilder,
        AiPromptLogRecorder promptLogRecorder,
        IServiceProvider serviceProvider)
        : base(dbContext)
    {
        this.availability = availability;
        this.authorizationService = authorizationService;
        this.options = options.Value;
        this.accessSearchExecutor = accessSearchExecutor;
        this.accessReviewBuilder = accessReviewBuilder;
        this.pseudonymizer = pseudonymizer;
        this.auditNarrativeBuilder = auditNarrativeBuilder;
        this.invocationRecorder = invocationRecorder;
        this.usageBuilder = usageBuilder;
        this.promptLogRecorder = promptLogRecorder;
        assistant = serviceProvider.GetService(typeof(IAiAssistant)) as IAiAssistant;
    }

    [HttpPost("access-search")]
    public async Task<ActionResult<AccessSearchResponse>> AccessSearchAsync(
        [FromBody] AccessSearchRequestBody body,
        CancellationToken cancellationToken)
    {
        if (!availability.Enabled || !availability.Features.AccessSearch || assistant is null)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "Natural-language access search is not enabled."));
        }

        string question = (body.Question ?? string.Empty).Trim();
        if (question.Length == 0)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "A question is required."));
        }

        if (question.Length > options.Limits.MaxPromptChars)
        {
            await promptLogRecorder.RecordAsync(
                PromptContext("accessSearch", null, question),
                AiPromptOutcomes.InvalidInput,
                $"The question exceeds the maximum of {options.Limits.MaxPromptChars} characters.");
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, $"The question exceeds the maximum of {options.Limits.MaxPromptChars} characters."));
        }

        // Scope to the applications the caller may view; the search never crosses that boundary.
        List<(Guid Id, string ApplicationId)> apps = await AccessibleApplicationsAsync(cancellationToken);
        if (apps.Count == 0)
        {
            return Ok(new AccessSearchResponse(string.Empty, "You do not have access to any applications.", [], []));
        }

        List<Guid> appRefIds = apps.Select(a => a.Id).ToList();

        // Ground the model in the combined vocabulary of every accessible application so it can plan
        // over values that actually exist somewhere in scope. Relevance-scoped to the question so the
        // request stays bounded even across many applications.
        AccessSearchVocabulary vocabulary = await accessSearchExecutor.BuildRelevantVocabularyAsync(
            appRefIds,
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
                null,
                token => assistant.PlanAccessSearchAsync(planRequest, token),
                cancellationToken);
            plannedForLog = plan;

            if (string.IsNullOrWhiteSpace(plan.Entity))
            {
                logOutcome = AiPromptOutcomes.Unmapped;
                logError = "The question could not be mapped to a valid access search.";
                return Ok(AiAssistController.BuildAccessSearchGuidance(
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

            // Resolve every filter value against the live data across all accessible applications before
            // executing. A value that resolves to a real key somewhere in scope is canonicalised; one
            // that cannot be resolved confidently yields a "did you mean" guidance rather than
            // misleading or empty results. This keeps correctness on the server, not in the prompt.
            AccessSearchValueResolution resolution = await accessSearchExecutor.ResolveFilterValuesAsync(
                appRefIds,
                spec,
                cancellationToken);
            if (!resolution.Success)
            {
                logOutcome = AiPromptOutcomes.ValidationFailed;
                logError = resolution.Error;
                return Ok(AiAssistController.BuildAccessSearchGuidance(
                    question,
                    vocabulary,
                    resolution.Error!,
                    plan.Suggestions));
            }

            spec = resolution.Spec!;

            // A relationship "include" (tree: parent + its children, e.g. a tenant and its applications)
            // over a cross-application entity must be resolved once over the whole accessible scope. A
            // tenant owns applications across several application scopes, so the per-application loop
            // below would attach only the children visible in a single iteration — and because
            // cross-application rows carry no application id, every iteration after the first is then
            // dropped by the loop's de-duplication, silently returning the tenant with only one of
            // several applications. Registry entities are resolved over the full scope in one pass;
            // built-in, application-partitioned entities keep the per-application path.
            if (!string.IsNullOrWhiteSpace(spec.Include)
                && AccessSearchExecutor.IsCrossApplicationEntity(spec.Entity))
            {
                AccessSearchOutcome includeOutcome = await accessSearchExecutor.ExecuteIncludePlatformAsync(
                    apps, spec, cancellationToken, authorizationService.CanAccessAllApplications(User));
                if (!includeOutcome.IsValid)
                {
                    logOutcome = AiPromptOutcomes.ValidationFailed;
                    logError = includeOutcome.Error ?? "The search could not be executed.";
                    return Ok(AiAssistController.BuildAccessSearchGuidance(
                        question,
                        vocabulary,
                        "That question referenced a field or filter I don't support yet.",
                        plan.Suggestions));
                }

                List<AccessSearchResultResponse> includeResults = includeOutcome.Rows
                    .Take(MaxTotalResults)
                    .Select(AiAssistController.MapAccessSearchRow)
                    .ToList();
                return Ok(new AccessSearchResponse(
                    plan.Entity,
                    plan.Explanation,
                    spec.Filters.Select(f => new AccessSearchFilterResponse(f.Field, f.Operator, f.Value)).ToList(),
                    includeResults,
                    includeOutcome.Mode));
            }

            // A relationship "group" over a cross-application entity (e.g. applications per tenant)
            // must be counted once over the whole accessible scope. A tenant owns applications across
            // several application scopes, so the per-application loop below would cap each tenant at
            // the apps visible in a single iteration — reporting "1 applications" for a tenant that
            // owns several. Registry entities are grouped over the full scope in one pass; built-in,
            // application-partitioned entities keep the per-application path.
            if (!string.IsNullOrWhiteSpace(spec.GroupByRelationship)
                && AccessSearchExecutor.IsCrossApplicationEntity(spec.Entity))
            {
                AccessSearchOutcome groupOutcome = await accessSearchExecutor.ExecuteGroupPlatformAsync(
                    apps, spec, cancellationToken, authorizationService.CanAccessAllApplications(User));
                if (!groupOutcome.IsValid)
                {
                    logOutcome = AiPromptOutcomes.ValidationFailed;
                    logError = groupOutcome.Error ?? "The search could not be executed.";
                    return Ok(AiAssistController.BuildAccessSearchGuidance(
                        question,
                        vocabulary,
                        "That question referenced a field or filter I don't support yet.",
                        plan.Suggestions));
                }

                List<AccessSearchResultResponse> groupResults = groupOutcome.Rows
                    .Take(MaxTotalResults)
                    .Select(AiAssistController.MapAccessSearchRow)
                    .ToList();
                return Ok(new AccessSearchResponse(
                    plan.Entity,
                    plan.Explanation,
                    spec.Filters.Select(f => new AccessSearchFilterResponse(f.Field, f.Operator, f.Value)).ToList(),
                    groupResults,
                    groupOutcome.Mode));
            }

            // A presence / compound-absence query ("has A", "has A but not B") over a cross-application
            // entity (e.g. "tenants with policies but no active users") must be evaluated once over the
            // whole accessible scope: a tenant with users in one application but not another would be a
            // false positive under the per-application loop. Registry (cross-application) entities are
            // handled here in a single pass; application-partitioned entities keep the per-app path.
            if ((!string.IsNullOrWhiteSpace(spec.PresentRelationship) || !string.IsNullOrWhiteSpace(spec.AbsentRelationship))
                && AccessSearchExecutor.IsCrossApplicationEntity(spec.Entity))
            {
                AccessSearchOutcome presenceOutcome = await accessSearchExecutor.ExecutePresenceAbsencePlatformAsync(
                    apps, spec, cancellationToken, authorizationService.CanAccessAllApplications(User));
                if (!presenceOutcome.IsValid)
                {
                    logOutcome = AiPromptOutcomes.ValidationFailed;
                    logError = presenceOutcome.Error ?? "The search could not be executed.";
                    return Ok(AiAssistController.BuildAccessSearchGuidance(
                        question,
                        vocabulary,
                        "That question referenced a field or filter I don't support yet.",
                        plan.Suggestions));
                }

                List<AccessSearchResultResponse> presenceResults = presenceOutcome.Rows
                    .Take(MaxTotalResults)
                    .Select(AiAssistController.MapAccessSearchRow)
                    .ToList();
                return Ok(new AccessSearchResponse(
                    plan.Entity,
                    plan.Explanation,
                    spec.Filters.Select(f => new AccessSearchFilterResponse(f.Field, f.Operator, f.Value)).ToList(),
                    presenceResults,
                    presenceOutcome.Mode));
            }

            // A field group-by (breakdown by an attribute, e.g. roles by risk level) is a single
            // aggregate over the whole accessible scope, so it is computed once — never summed across
            // the per-application loop below (which would split each value into one bucket per app).
            if (!string.IsNullOrWhiteSpace(spec.GroupByField))
            {
                AccessSearchOutcome fieldGroupOutcome = await accessSearchExecutor.ExecuteFieldGroupPlatformAsync(
                    apps, spec, cancellationToken, authorizationService.CanAccessAllApplications(User));
                if (!fieldGroupOutcome.IsValid)
                {
                    logOutcome = AiPromptOutcomes.ValidationFailed;
                    logError = fieldGroupOutcome.Error ?? "The search could not be executed.";
                    return Ok(AiAssistController.BuildAccessSearchGuidance(
                        question,
                        vocabulary,
                        "That question referenced a field or filter I don't support yet.",
                        plan.Suggestions));
                }

                List<AccessSearchResultResponse> fieldGroupResults = fieldGroupOutcome.Rows
                    .Take(MaxTotalResults)
                    .Select(AiAssistController.MapAccessSearchRow)
                    .ToList();
                return Ok(new AccessSearchResponse(
                    plan.Entity,
                    plan.Explanation,
                    spec.Filters.Select(f => new AccessSearchFilterResponse(f.Field, f.Operator, f.Value)).ToList(),
                    fieldGroupResults,
                    fieldGroupOutcome.Mode));
            }

            // A plain records lookup or scalar count over a cross-application entity must be resolved
            // once over the whole accessible scope. Some cross-application entities (AI invocation /
            // prompt-log rows) carry no owning application at all — they record platform-wide Ask AI
            // activity, not per-application activity — so the per-application loop below, whose scope
            // is one application at a time, can never see them and would silently return nothing.
            // Registry entities are resolved over the full scope in one pass here; built-in,
            // application-partitioned entities keep the per-application path.
            if (AccessSearchExecutor.IsCrossApplicationEntity(spec.Entity))
            {
                AccessSearchOutcome recordsOutcome = await accessSearchExecutor.ExecuteRecordsPlatformAsync(
                    apps, spec, cancellationToken, authorizationService.CanAccessAllApplications(User));
                if (!recordsOutcome.IsValid)
                {
                    logOutcome = AiPromptOutcomes.ValidationFailed;
                    logError = recordsOutcome.Error ?? "The search could not be executed.";
                    return Ok(AiAssistController.BuildAccessSearchGuidance(
                        question,
                        vocabulary,
                        "That question referenced a field or filter I don't support yet.",
                        plan.Suggestions));
                }

                List<AccessSearchResultResponse> recordsResults = recordsOutcome.Rows
                    .Take(MaxTotalResults)
                    .Select(AiAssistController.MapAccessSearchRow)
                    .ToList();
                return Ok(new AccessSearchResponse(
                    plan.Entity,
                    plan.Explanation,
                    spec.Filters.Select(f => new AccessSearchFilterResponse(f.Field, f.Operator, f.Value)).ToList(),
                    recordsResults,
                    recordsOutcome.Mode));
            }

            var results = new List<AccessSearchResultResponse>();
            // For a scalar count we merge a bounded, de-duplicated sample of the matching rows so the
            // aggregate stays auditable across the whole platform. The authoritative total is computed
            // once over the full multi-application scope (see below) rather than summed per application,
            // so a cross-application row (e.g. a tenant owning several apps) is never double-counted.
            var countSample = new List<AccessSearchResultResponse>();
            // Cross-application entities (e.g. tenant) can surface the same row while iterating each
            // accessible app; de-duplicate identical rows so they appear once.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string resultMode = AccessSearchModes.Records;
            foreach ((Guid appRefId, string applicationId) in apps)
            {
                AccessSearchOutcome outcome = await accessSearchExecutor.ExecuteAsync(
                    appRefId,
                    applicationId,
                    spec,
                    cancellationToken);

                // The spec is validated once against the closed schema; a validation failure applies to
                // every application, so surface it immediately rather than silently returning nothing.
                if (!outcome.IsValid)
                {
                    logOutcome = AiPromptOutcomes.ValidationFailed;
                    logError = outcome.Error ?? "The search could not be executed.";
                    return Ok(AiAssistController.BuildAccessSearchGuidance(
                        question,
                        vocabulary,
                        "That question referenced a field or filter I don't support yet.",
                        plan.Suggestions));
                }

                resultMode = outcome.Mode;

                // Count mode: each application returns a single count row whose children are that app's
                // sample rows. Merge the de-duplicated samples into one platform-wide answer; the total
                // is computed separately over the full scope so shared rows are not counted per app.
                if (outcome.Mode == AccessSearchModes.Count)
                {
                    foreach (AccessSearchRow row in outcome.Rows)
                    {
                        foreach (AccessSearchRow child in row.Children ?? [])
                        {
                            if (countSample.Count >= MaxTotalResults)
                            {
                                break;
                            }

                            if (seen.Add($"{child.EntityType}|{child.Title}|{child.DeepLinkKey}|{child.ApplicationId}"))
                            {
                                countSample.Add(AiAssistController.MapAccessSearchRow(child));
                            }
                        }
                    }

                    continue;
                }

                foreach (AccessSearchRow row in outcome.Rows)
                {
                    if (!seen.Add($"{row.EntityType}|{row.Title}|{row.DeepLinkKey}|{row.ApplicationId}"))
                    {
                        continue;
                    }

                    results.Add(AiAssistController.MapAccessSearchRow(row));

                    if (results.Count >= MaxTotalResults)
                    {
                        break;
                    }
                }

                if (results.Count >= MaxTotalResults)
                {
                    break;
                }
            }

            // Collapse the accumulated count into a single platform-wide count row (with its merged
            // sample as children) so the UI shows one total, not one number per application. The total
            // is a single distinct count over the whole scope, so a row shared across applications
            // (e.g. a tenant) is counted once — never inflated by the per-application iteration.
            if (resultMode == AccessSearchModes.Count)
            {
                long countTotal = await accessSearchExecutor.CountPlatformAsync(apps, spec, cancellationToken, authorizationService.CanAccessAllApplications(User));
                results =
                [
                    new AccessSearchResultResponse(
                        string.Empty,
                        "COUNT",
                        countTotal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        $"{spec.Entity} record(s) matching the query",
                        null,
                        null,
                        countSample.Count > 0 ? countSample : null),
                ];
            }

            // A relationship-count group (e.g. "top 5 roles by permission count") over an application-
            // partitioned entity is computed per application above, each already sorted and capped
            // within its own app. Once merged across every accessible application the combined list must
            // be re-sorted by count (parsed from the leading number in Detail, e.g. "12 permission(s)
            // granted") and re-capped to the caller's limit — otherwise a platform with several
            // applications returns every app's groups concatenated, unsorted and uncapped, instead of
            // the platform-wide top N.
            if (resultMode == AccessSearchModes.Group)
            {
                results = results
                    .OrderByDescending(ParseLeadingCount)
                    .Take(Math.Min(EffectiveResultLimit(spec.Limit), MaxTotalResults))
                    .ToList();
            }

            // A caller-requested limit ("show N" / "top N") caps the merged record list platform-wide.
            // Group/field-group and count results are already bounded by the executor, so this only
            // trims the per-application records merge.
            if (resultMode == AccessSearchModes.Records && spec.Limit is int recordLimit && recordLimit > 0)
            {
                results = results.Take(Math.Min(recordLimit, MaxTotalResults)).ToList();
            }

            return Ok(new AccessSearchResponse(
                plan.Entity,
                plan.Explanation,
                spec.Filters.Select(f => new AccessSearchFilterResponse(f.Field, f.Operator, f.Value)).ToList(),
                results,
                resultMode));
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
                PromptContext("accessSearch", null, question),
                logOutcome,
                logError,
                plannedForLog is null ? null : new { plannedForLog.Entity, plannedForLog.Explanation, plannedForLog.Filters });
        }
    }

    [HttpPost("access-review/summarize")]
    public async Task<ActionResult<AccessReviewResponse>> SummarizeAccessReviewAsync(
        [FromBody] AccessReviewRequestBody body,
        CancellationToken cancellationToken)
    {
        // The deterministic review is available whenever the certification feature is on. The model
        // only narrates and prioritizes it, so this endpoint still returns the raw review (with an
        // empty summary and no per-item rationale) when no AI provider is configured.
        if (!availability.Enabled || !availability.Features.AccessCertification)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "Access certification is not enabled."));
        }

        string subjectEmail = (body.SubjectEmail ?? string.Empty).Trim();
        if (subjectEmail.Length == 0)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "A subjectEmail is required."));
        }

        // Scope to the applications the caller may view; optionally narrow to a single application.
        List<(Guid Id, string ApplicationId)> apps = await AccessibleApplicationsAsync(cancellationToken);
        string? applicationId = string.IsNullOrWhiteSpace(body.ApplicationId) ? null : body.ApplicationId.Trim();
        if (applicationId is not null)
        {
            apps = apps
                .Where(a => string.Equals(a.ApplicationId, applicationId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (apps.Count == 0)
        {
            return Ok(new AccessReviewResponse(subjectEmail, null, []));
        }

        var scope = apps.Select(a => new AccessReviewScope(a.Id, a.ApplicationId)).ToList();
        AccessReview review = await accessReviewBuilder.BuildAsync(subjectEmail, scope, cancellationToken);

        string? summary = null;
        var rationaleById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (assistant is not null && review.Items.Count > 0)
        {
            // PII boundary: the subject is referenced only by a pseudonym and items by opaque ids, so
            // no email address is ever sent to the model.
            string label = pseudonymizer.Pseudonymize(subjectEmail);
            var summaryRequest = new AccessReviewSummaryRequest(
                SubjectLabel: label,
                ItemCount: review.Items.Count,
                KeepCount: review.Items.Count(item => item.Recommendation == "KEEP"),
                RevokeCount: review.Items.Count(item => item.Recommendation == "REVOKE"),
                ReviewCount: review.Items.Count(item => item.Recommendation == "REVIEW"),
                Items: review.Items
                    .Select(item => new AccessReviewItemFact(
                        item.Id,
                        item.ApplicationId,
                        item.RoleKey,
                        item.RoleName,
                        item.Privileged,
                        item.RiskLevel,
                        item.Recommendation,
                        item.RecommendationReason,
                        item.LastUsedDaysAgo,
                        item.Dormant,
                        item.PeerCount,
                        item.Permissions))
                    .ToList());

            AccessReviewSummaryResult result = await InvokeWithTimeoutAsync(
                "accessCertification",
                body.ApplicationId,
                token => assistant.SummarizeAccessReviewAsync(summaryRequest, token),
                cancellationToken);

            summary = result.Summary;
            rationaleById = result.Rationales
                .GroupBy(rationale => rationale.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Rationale, StringComparer.OrdinalIgnoreCase);
        }

        List<AccessReviewItemResponse> responseItems = review.Items
            .Select(item => new AccessReviewItemResponse(
                item.Id,
                item.ApplicationId,
                item.RoleKey,
                item.RoleName,
                item.Privileged,
                item.RiskLevel,
                item.Status,
                item.ValidFrom,
                item.ValidUntil,
                item.Source,
                item.Reason,
                item.LastUsedAt,
                item.LastUsedDaysAgo,
                item.Dormant,
                item.PeerCount,
                item.Permissions,
                item.Recommendation,
                item.RecommendationReason,
                rationaleById.TryGetValue(item.Id, out string? rationale) ? rationale : null))
            .ToList();

        return Ok(new AccessReviewResponse(subjectEmail, summary, responseItems));
    }

    [HttpPost("audit/narrative")]
    public async Task<ActionResult<AuditNarrativeResponse>> NarrateAuditAsync(
        [FromBody] AuditNarrativeRequestBody body,
        CancellationToken cancellationToken)
    {
        // The deterministic change feed is available whenever the feature is on. The model only
        // narrates it, so this endpoint still returns the raw events (with a null summary and no
        // sections) when no AI provider is configured.
        if (!availability.Enabled || !availability.Features.AuditNarrative)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "Audit narrative is not enabled."));
        }

        // Window: default to the trailing 30 days when unspecified; reject an inverted range.
        DateTimeOffset toUtc = body.ToUtc ?? DateTimeOffset.UtcNow;
        DateTimeOffset fromUtc = body.FromUtc ?? toUtc.AddDays(-30);
        if (fromUtc > toUtc)
        {
            return UnprocessableEntity(Error(GovernanceErrorCodes.ValidationError, "fromUtc must be on or before toUtc."));
        }

        string? applicationId = string.IsNullOrWhiteSpace(body.ApplicationId) ? null : body.ApplicationId.Trim();
        if (applicationId is not null
            && !authorizationService.CanAccessAllApplications(User)
            && !authorizationService.IsAuthorized(User, applicationId, DelegatedAdminCapability.ViewAudit))
        {
            return StatusCode(StatusCodes.Status403Forbidden, Error(GovernanceErrorCodes.Forbidden, "You are not permitted to view this application's audit events."));
        }

        // Scope to the applications the caller may audit; the narrative never crosses that boundary.
        List<string> auditableAppIds = await AuditableApplicationIdsAsync(cancellationToken);
        if (auditableAppIds.Count == 0)
        {
            return Ok(new AuditNarrativeResponse(fromUtc, toUtc, 0, null, [], [], [], [], []));
        }

        string? actorEmail = string.IsNullOrWhiteSpace(body.ActorEmail) ? null : body.ActorEmail.Trim();
        string? eventType = string.IsNullOrWhiteSpace(body.EventType) ? null : body.EventType.Trim();

        var query = new AuditNarrativeQuery(fromUtc, toUtc, applicationId, actorEmail, eventType);
        AuditNarrative narrative = await auditNarrativeBuilder.BuildAsync(auditableAppIds, query, cancellationToken);

        string? summary = null;
        var sections = new List<AuditNarrativeSectionResponse>();
        if (assistant is not null && narrative.Events.Count > 0)
        {
            // PII boundary: a per-request pseudonym map keeps actor/target emails out of the model.
            // The narrative is de-pseudonymized afterwards for the authorized auditor, and any email
            // inside a free-text reason is redacted before it is sent.
            var pseudonyms = new AuditPseudonymMap();
            var eventFacts = narrative.Events
                .Select(e => new AuditEventFact(
                    e.EventId,
                    e.EventType,
                    e.ApplicationId,
                    pseudonyms.LabelFor(e.ActorEmail),
                    pseudonyms.LabelFor(e.TargetSubjectEmail),
                    e.Timestamp,
                    RedactEmails(e.Reason)))
                .ToList();

            var request = new AuditNarrationRequest(
                fromUtc,
                toUtc,
                narrative.Events.Count,
                narrative.ByApplication.Select(g => new AuditGroupFact(g.Key, g.Count)).ToList(),
                narrative.ByActor.Select(g => new AuditGroupFact(pseudonyms.LabelFor(g.Key) ?? g.Key, g.Count)).ToList(),
                narrative.ByEventType.Select(g => new AuditGroupFact(g.Key, g.Count)).ToList(),
                narrative.TopDenyReasons.Select(r => new AuditDenyFact(r.ApplicationId, r.DenyReason, r.Count)).ToList(),
                eventFacts);

            AuditNarrationResult result = await InvokeWithTimeoutAsync(
                "auditNarrative",
                applicationId,
                token => assistant.NarrateAuditAsync(request, token),
                cancellationToken);

            summary = pseudonyms.Restore(result.Summary);
            sections = result.Sections
                .Select(section => new AuditNarrativeSectionResponse(
                    pseudonyms.Restore(section.Heading),
                    pseudonyms.Restore(section.Detail),
                    section.EventIds))
                .ToList();
        }

        return Ok(new AuditNarrativeResponse(
            narrative.FromUtc,
            narrative.ToUtc,
            narrative.Events.Count,
            summary,
            sections,
            narrative.Events.Select(AuditNarrativeEventResponse.From).ToList(),
            narrative.ByApplication.Select(g => new AuditGroupCountResponse(g.Key, g.Count)).ToList(),
            narrative.ByEventType.Select(g => new AuditGroupCountResponse(g.Key, g.Count)).ToList(),
            narrative.TopDenyReasons.Select(r => new AuditDenyReasonResponse(r.ApplicationId, r.DenyReason, r.Count)).ToList()));
    }

    [HttpGet("usage")]
    public async Task<ActionResult<AiUsageResponse>> GetUsageAsync(
        [FromQuery] int? windowDays,
        CancellationToken cancellationToken)
    {
        // Metadata-only observability over the AI invocation log. Available whenever AI is
        // configured (even in Fake mode) and disappears (404) when AI is off, mirroring the other
        // surfaces. Reads are scoped to the applications the caller may audit.
        if (!availability.Enabled)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "AI assistance is not enabled."));
        }

        int days = windowDays is > 0 and <= 90 ? windowDays.Value : 30;
        DateTimeOffset toUtc = DateTimeOffset.UtcNow;
        DateTimeOffset fromUtc = toUtc.AddDays(-days);

        List<string> auditableAppIds = await AuditableApplicationIdsAsync(cancellationToken);
        bool includePlatformScoped = authorizationService.CanAccessAllApplications(User);

        AiUsageReport report = await usageBuilder.BuildAsync(
            auditableAppIds,
            includePlatformScoped,
            fromUtc,
            toUtc,
            cancellationToken);
        return Ok(new AiUsageResponse(days, report));
    }

    [HttpGet("prompt-logs")]
    public async Task<ActionResult<AiPromptLogResponse>> GetPromptLogsAsync(
        [FromQuery] int? windowDays,
        [FromQuery] string? feature,
        [FromQuery] string? outcome,
        [FromQuery] bool? failuresOnly,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        // Review surface for free-text AI prompts (access search, SoD draft, policy authoring),
        // including the validation failures the metadata-only usage log cannot see. Unlike the usage
        // log this exposes prompt text, so it is admin-gated (ViewAudit) and scoped to the caller's
        // auditable applications; platform-scoped rows are visible only to full platform admins.
        if (!availability.Enabled)
        {
            return NotFound(Error(GovernanceErrorCodes.ValidationError, "AI assistance is not enabled."));
        }

        int days = windowDays is > 0 and <= 90 ? windowDays.Value : 30;
        DateTimeOffset fromUtc = DateTimeOffset.UtcNow.AddDays(-days);

        List<string> auditableAppIds = await AuditableApplicationIdsAsync(cancellationToken);
        bool includePlatformScoped = authorizationService.CanAccessAllApplications(User);

        IQueryable<AiPromptLogEntity> query = DbContext.AiPromptLogs.AsNoTracking()
            .Where(log => log.Timestamp >= fromUtc)
            .Where(log =>
                (log.ApplicationId != null && auditableAppIds.Contains(log.ApplicationId))
                || (log.ApplicationId == null && includePlatformScoped));

        if (!string.IsNullOrWhiteSpace(feature))
        {
            string featureFilter = feature.Trim();
            query = query.Where(log => log.Feature == featureFilter);
        }

        if (!string.IsNullOrWhiteSpace(outcome))
        {
            string outcomeFilter = outcome.Trim();
            query = query.Where(log => log.Outcome == outcomeFilter);
        }

        if (failuresOnly == true)
        {
            query = query.Where(log => log.Outcome != AiPromptOutcomes.Succeeded);
        }

        // The prompt log grows without bound, so it is always paged (default 25/page) rather than
        // silently truncated. Total lets the UI show a real page count.
        int size = pageSize is int s && s > 0 ? Math.Min(s, PageRequest.MaxPageSize) : 25;
        int pageNumber = page is int p && p > 0 ? p : 1;
        int total = await query.CountAsync(cancellationToken);
        List<AiPromptLogItemResponse> items = await query
            .OrderByDescending(log => log.Timestamp)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .Select(log => new AiPromptLogItemResponse(
                log.Id,
                log.Feature,
                log.ActorEmail,
                log.ActorRole,
                log.ApplicationId,
                log.PromptText,
                log.Outcome,
                log.ErrorMessage,
                log.Interpretation,
                log.Provider,
                log.Model,
                log.Timestamp,
                log.CorrelationId))
            .ToListAsync(cancellationToken);

        return Ok(new AiPromptLogResponse(days, pageNumber, size, total, items));
    }

    private async Task<List<string>> AuditableApplicationIdsAsync(CancellationToken cancellationToken)
    {
        List<string> all = await DbContext.Applications.AsNoTracking()
            .Select(a => a.ApplicationId)
            .ToListAsync(cancellationToken);
        if (authorizationService.CanAccessAllApplications(User))
        {
            return all;
        }

        return all
            .Where(id => authorizationService.IsAuthorized(User, id, DelegatedAdminCapability.ViewAudit))
            .ToList();
    }

    // Replaces any email address inside admin free-text (an audit reason) with a placeholder so no
    // personal data reaches the model, even indirectly.
    private static string? RedactEmails(string? text) =>
        text is null ? null : EmailPattern.Replace(text, "[email]");

    private static readonly Regex EmailPattern =
        new(@"[^\s@]+@[^\s@]+\.[^\s@]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A per-request, reversible map of email -> pseudonymous label. Labels are sent to the model in
    // place of PII; the narrative is restored for the authorized auditor once the model returns.
    private sealed class AuditPseudonymMap
    {
        private readonly Dictionary<string, string> labelByEmail = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Label, string Email)> restorations = [];

        public string? LabelFor(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return null;
            }

            if (!labelByEmail.TryGetValue(email, out string? label))
            {
                label = "person-" + (labelByEmail.Count + 1);
                labelByEmail[email] = label;
                restorations.Add((label, email));
            }

            return label;
        }

        public string Restore(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            // Longest labels first so "person-1" does not partially match inside "person-10".
            foreach ((string label, string email) in restorations.OrderByDescending(r => r.Label.Length))
            {
                text = text.Replace(label, email, StringComparison.OrdinalIgnoreCase);
            }

            return text;
        }
    }

    // Parses the leading integer from a group row's Detail text (e.g. "12 permission(s) granted" → 12),
    // used only to re-rank merged group rows across applications by count. Falls back to 0 (sorts last)
    // when the text does not start with a number — never throws on unexpected formats.
    private static readonly Regex LeadingCountPattern = new(@"^(\d+)", RegexOptions.Compiled);

    private static int ParseLeadingCount(AccessSearchResultResponse row)
    {
        Match match = LeadingCountPattern.Match(row.Detail ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, out int count) ? count : 0;
    }

    // Resolves the caller-requested limit for a merged, platform-wide result list: a positive limit is
    // honored; anything else falls back to the platform-wide cap.
    private static int EffectiveResultLimit(int? limit) => limit is int n && n > 0 ? n : MaxTotalResults;

    private async Task<List<(Guid Id, string ApplicationId)>> AccessibleApplicationsAsync(CancellationToken cancellationToken)
    {
        List<(Guid Id, string ApplicationId)> all = await DbContext.Applications.AsNoTracking()
            .Select(a => new ValueTuple<Guid, string>(a.Id, a.ApplicationId))
            .ToListAsync(cancellationToken);
        if (authorizationService.CanAccessAllApplications(User))
        {
            return all;
        }

        return all
            .Where(a => authorizationService.IsAuthorized(User, a.ApplicationId, DelegatedAdminCapability.ReadOnlyView))
            .ToList();
    }

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
}

/// <summary>
/// Platform AI usage report (Tier 1 observability). Wraps the aggregated, metadata-only
/// <see cref="AiUsageReport"/> together with the resolved trailing window in days.
/// </summary>
public sealed record AiUsageResponse(int WindowDays, AiUsageReport Report);

/// <summary>
/// A page of free-text AI prompt-log rows (F1/F7/F8) for admin review, together with the resolved
/// trailing window in days. Unlike the metadata-only usage report this exposes the submitted prompt
/// text and the model's interpretation.
/// </summary>
public sealed record AiPromptLogResponse(int WindowDays, int Page, int PageSize, int Total, IReadOnlyList<AiPromptLogItemResponse> Items);

/// <summary>A single free-text AI prompt-log row.</summary>
public sealed record AiPromptLogItemResponse(
    Guid Id,
    string Feature,
    string? ActorEmail,
    string? ActorRole,
    string? ApplicationId,
    string PromptText,
    string Outcome,
    string? ErrorMessage,
    string? Interpretation,
    string Provider,
    string? Model,
    DateTimeOffset Timestamp,
    string? CorrelationId);

/// <summary>Request for a subject access-certification review (F4). Optionally scoped to one app.</summary>
public sealed class AccessReviewRequestBody
{
    [Required]
    public string? SubjectEmail { get; init; }

    public string? ApplicationId { get; init; }
}

/// <summary>
/// The result of a subject access review: an optional AI summary (null when the model is off) plus
/// every active grant with its deterministic recommendation and, where available, an AI rationale.
/// </summary>
public sealed record AccessReviewResponse(
    string SubjectEmail,
    string? Summary,
    IReadOnlyList<AccessReviewItemResponse> Items);

/// <summary>A single reviewed grant returned to the portal.</summary>
public sealed record AccessReviewItemResponse(
    string Id,
    string ApplicationId,
    string RoleKey,
    string RoleName,
    bool Privileged,
    string RiskLevel,
    string Status,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidUntil,
    string Source,
    string? Reason,
    DateTimeOffset? LastUsedAt,
    int? LastUsedDaysAgo,
    bool Dormant,
    int PeerCount,
    IReadOnlyList<string> Permissions,
    string Recommendation,
    string RecommendationReason,
    string? Rationale);

/// <summary>Request for an audit-narrative (compliance-evidence) summary (F9). All fields optional.</summary>
public sealed class AuditNarrativeRequestBody
{
    /// <summary>Window start (inclusive). Defaults to 30 days before <see cref="ToUtc"/>.</summary>
    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>Window end (inclusive). Defaults to now.</summary>
    public DateTimeOffset? ToUtc { get; init; }

    public string? ApplicationId { get; init; }

    public string? ActorEmail { get; init; }

    public string? EventType { get; init; }
}

/// <summary>
/// An audit-narrative result: the deterministic change feed for the window (with real identities,
/// for the authorized auditor) plus an optional AI summary and grounded sections (null/empty when
/// the model is off).
/// </summary>
public sealed record AuditNarrativeResponse(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int TotalEvents,
    string? Summary,
    IReadOnlyList<AuditNarrativeSectionResponse> Sections,
    IReadOnlyList<AuditNarrativeEventResponse> Events,
    IReadOnlyList<AuditGroupCountResponse> ByApplication,
    IReadOnlyList<AuditGroupCountResponse> ByEventType,
    IReadOnlyList<AuditDenyReasonResponse> TopDenyReasons);

/// <summary>A titled section of the narrative citing the concrete event ids it covers.</summary>
public sealed record AuditNarrativeSectionResponse(
    string Heading,
    string Detail,
    IReadOnlyList<string> EventIds);

/// <summary>A single audit event with its concrete evidence, returned to the authorized auditor.</summary>
public sealed record AuditNarrativeEventResponse(
    string EventId,
    string EventType,
    string? ApplicationId,
    string? ActorEmail,
    string? TargetSubjectEmail,
    DateTimeOffset Timestamp,
    string? OldValue,
    string? NewValue,
    string? Reason)
{
    public static AuditNarrativeEventResponse From(AuditNarrativeEvent e) => new(
        e.EventId,
        e.EventType,
        e.ApplicationId,
        e.ActorEmail,
        e.TargetSubjectEmail,
        e.Timestamp,
        e.OldValue,
        e.NewValue,
        e.Reason);
}

/// <summary>A grouped count (by application or event type).</summary>
public sealed record AuditGroupCountResponse(string Key, int Count);

/// <summary>A deny-reason aggregate for one application in the window.</summary>
public sealed record AuditDenyReasonResponse(string ApplicationId, string DenyReason, int Count);

