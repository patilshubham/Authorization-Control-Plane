using Authorization.Ai;
using Authorization.Api.Authorization;
using Authorization.Api.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Authorization.Api.Controllers;

/// <summary>
/// Exposes server-owned runtime configuration to the portal. The server is the single
/// source of truth for feature availability (notably AI), so the client never infers
/// these flags locally.
/// </summary>
[ApiController]
[Authorize(Policy = DelegatedAdminPolicyNames.AdminApi)]
[Route("v1/config")]
public sealed class ConfigController : ControllerBase
{
    private readonly AiAvailability aiAvailability;
    private readonly AiOptions aiOptions;
    private readonly PortalOptions portalOptions;

    public ConfigController(
        AiAvailability aiAvailability,
        IOptions<AiOptions> aiOptions,
        IOptions<PortalOptions> portalOptions)
    {
        this.aiAvailability = aiAvailability;
        this.aiOptions = aiOptions.Value;
        this.portalOptions = portalOptions.Value;
    }

    [HttpGet]
    public ActionResult<PortalConfigResponse> GetConfig()
    {
        // Runtime provenance is surfaced only when AI is actually enabled, so a disabled or
        // misconfigured provider never leaks a provider/model/limits shape. The API key is never
        // part of this response.
        bool enabled = aiAvailability.Enabled;
        bool live = enabled && !string.Equals(aiAvailability.Provider, AiProviders.Fake, StringComparison.OrdinalIgnoreCase);

        var ai = new AiConfigResponse(
            Enabled: enabled,
            Provider: enabled ? aiAvailability.Provider : null,
            // The Fake provider is deterministic and backed by no model.
            Model: live ? aiOptions.AzureOpenAI.ChatDeployment : null,
            Limits: enabled
                ? new AiLimitsResponse(
                    RequestTimeoutSeconds: aiOptions.Limits.RequestTimeoutSeconds,
                    MaxTokens: aiOptions.Limits.MaxTokens,
                    MaxPromptChars: aiOptions.Limits.MaxPromptChars,
                    Temperature: aiOptions.Limits.Temperature)
                : null,
            Features: new AiFeatureFlagsResponse(
                PolicyAuthoring: aiAvailability.Features.PolicyAuthoring,
                DecisionExplainer: aiAvailability.Features.DecisionExplainer,
                ImpactAnalysis: aiAvailability.Features.ImpactAnalysis,
                ConfigAdvisor: aiAvailability.Features.ConfigAdvisor,
                AccessSearch: aiAvailability.Features.AccessSearch,
                SodAnalysis: aiAvailability.Features.SodAnalysis,
                AccessCertification: aiAvailability.Features.AccessCertification,
                AuditNarrative: aiAvailability.Features.AuditNarrative),
            // Per-feature sampling temperature, surfaced only when AI is enabled so the portal can
            // show the active temperature for each capability. These are non-sensitive tuning knobs.
            FeatureTemperatures: enabled
                ? new AiFeatureTemperaturesResponse(
                    PolicyAuthoring: aiOptions.Features.PolicyAuthoring.Temperature,
                    DecisionExplainer: aiOptions.Features.DecisionExplainer.Temperature,
                    ImpactAnalysis: aiOptions.Features.ImpactAnalysis.Temperature,
                    ConfigAdvisor: aiOptions.Features.ConfigAdvisor.Temperature,
                    AccessSearch: aiOptions.Features.AccessSearch.Temperature,
                    SodAnalysis: aiOptions.Features.SodAnalysis.Temperature,
                    AccessCertification: aiOptions.Features.AccessCertification.Temperature,
                    AuditNarrative: aiOptions.Features.AuditNarrative.Temperature)
                : null);

        var pagination = new PaginationConfigResponse(
            DefaultPageSize: portalOptions.Pagination.DefaultPageSize,
            MaxPageSize: portalOptions.Pagination.MaxPageSize,
            PageSizeOptions: portalOptions.Pagination.PageSizeOptions);

        var cache = new CacheConfigResponse(
            DefaultStaleMs: portalOptions.Cache.DefaultStaleMs,
            VolatileStaleMs: portalOptions.Cache.VolatileStaleMs,
            ConfigStaleMs: portalOptions.Cache.ConfigStaleMs);

        var ui = new UiConfigResponse(
            AiReportingWindows: portalOptions.Ui.AiReportingWindows,
            ActivityTrendDays: portalOptions.Ui.ActivityTrendDays,
            AuditPageSize: portalOptions.Ui.AuditPageSize);

        return Ok(new PortalConfigResponse(ai, pagination, cache, ui, portalOptions.RoleLabels));
    }
}

public sealed record PortalConfigResponse(
    AiConfigResponse Ai,
    PaginationConfigResponse Pagination,
    CacheConfigResponse Cache,
    UiConfigResponse Ui,
    IReadOnlyDictionary<string, string> RoleLabels);

/// <summary>Pagination defaults and allowed page-size choices for portal tables.</summary>
public sealed record PaginationConfigResponse(
    int DefaultPageSize,
    int MaxPageSize,
    IReadOnlyList<int> PageSizeOptions);

/// <summary>Client cache freshness windows (milliseconds) for React Query staleness.</summary>
public sealed record CacheConfigResponse(
    int DefaultStaleMs,
    int VolatileStaleMs,
    int ConfigStaleMs);

/// <summary>Miscellaneous UI windows/limits owned by the server.</summary>
public sealed record UiConfigResponse(
    IReadOnlyList<int> AiReportingWindows,
    int ActivityTrendDays,
    int AuditPageSize);

public sealed record AiConfigResponse(
    bool Enabled,
    string? Provider,
    string? Model,
    AiLimitsResponse? Limits,
    AiFeatureFlagsResponse Features,
    AiFeatureTemperaturesResponse? FeatureTemperatures);

/// <summary>Non-sensitive runtime guardrails for the AI surface.</summary>
public sealed record AiLimitsResponse(int RequestTimeoutSeconds, int MaxTokens, int MaxPromptChars, double Temperature);

public sealed record AiFeatureFlagsResponse(
    bool PolicyAuthoring,
    bool DecisionExplainer,
    bool ImpactAnalysis,
    bool ConfigAdvisor,
    bool AccessSearch,
    bool SodAnalysis,
    bool AccessCertification,
    bool AuditNarrative);

/// <summary>Per-feature sampling temperature, present only when AI is enabled.</summary>
public sealed record AiFeatureTemperaturesResponse(
    double PolicyAuthoring,
    double DecisionExplainer,
    double ImpactAnalysis,
    double ConfigAdvisor,
    double AccessSearch,
    double SodAnalysis,
    double AccessCertification,
    double AuditNarrative);
