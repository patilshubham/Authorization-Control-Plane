namespace Authorization.Ai;

/// <summary>
/// Configuration for the opt-in AI assistance features. Bound from the <c>Ai</c>
/// configuration section. The whole surface is disabled by default so the product
/// is fully functional with no AI provider configured.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>Master switch. When <c>false</c> no AI services are registered and every AI endpoint is absent.</summary>
    public bool Enabled { get; init; }

    /// <summary>Provider selector: <c>AzureOpenAI</c>, <c>OpenAI</c> (OpenAI-compatible, e.g. GitHub Models) or <c>Fake</c> (deterministic stub).</summary>
    public string Provider { get; init; } = AiProviders.AzureOpenAI;

    public AzureOpenAiOptions AzureOpenAI { get; init; } = new();

    public AiFeatureOptions Features { get; init; } = new();

    public AiLimitsOptions Limits { get; init; } = new();

    public AiLoggingOptions Logging { get; init; } = new();
}

/// <summary>Well-known provider identifiers accepted by <see cref="AiOptions.Provider"/>.</summary>
public static class AiProviders
{
    public const string AzureOpenAI = "AzureOpenAI";

    /// <summary>OpenAI-compatible endpoint (also used for GitHub Models during development).</summary>
    public const string OpenAI = "OpenAI";

    /// <summary>Deterministic in-process stub used for tests and offline development.</summary>
    public const string Fake = "Fake";
}

public sealed class AzureOpenAiOptions
{
    /// <summary>Resource endpoint, e.g. <c>https://&lt;resource&gt;.openai.azure.com/</c>, or an OpenAI-compatible base URL.</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>Secret. Sourced from environment / secret store only — never committed or logged.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Chat/completions deployment (Azure) or model name (OpenAI-compatible).</summary>
    public string ChatDeployment { get; init; } = "gpt-4o-mini";

    /// <summary>Embedding deployment/model. Used by the deferred RAG feature only.</summary>
    public string EmbeddingDeployment { get; init; } = "text-embedding-3-small";

    public string ApiVersion { get; init; } = "2024-10-21";

    /// <summary>Set to <c>false</c> for reasoning-style chat deployments (e.g. gpt-5.5, o-series)
    /// that reject any non-default temperature value with a hard 400. When <c>false</c>, the
    /// <c>temperature</c> field is omitted from the request entirely so the provider applies its
    /// own default.</summary>
    public bool SupportsTemperature { get; init; } = true;

    /// <summary>Set to <c>false</c> for reasoning-style chat deployments (e.g. gpt-5.5, o-series)
    /// that reject the legacy <c>max_tokens</c> request field with a hard 400 and require
    /// <c>max_completion_tokens</c> instead.</summary>
    public bool SupportsMaxTokens { get; init; } = true;
}

public sealed class AiFeatureOptions
{
    /// <summary>F1 — Policy Authoring Copilot. Structured JSON extraction, so it runs at temperature 0 for determinism.</summary>
    public AiFeatureSetting PolicyAuthoring { get; init; } = new() { Enabled = true, Temperature = 0 };

    /// <summary>F2 — Decision Explainer. Narrative output, so it runs at the advisory temperature by default.</summary>
    public AiFeatureSetting DecisionExplainer { get; init; } = new() { Enabled = true, Temperature = 0.2 };

    /// <summary>F5 — Pre-publish Impact / blast-radius analysis. Deterministic core, narrative summary at the advisory temperature.</summary>
    public AiFeatureSetting ImpactAnalysis { get; init; } = new() { Enabled = true, Temperature = 0.2 };

    /// <summary>F6 — Governance Advisor / Config Linter. Deterministic core, narrative summary at the advisory temperature.</summary>
    public AiFeatureSetting ConfigAdvisor { get; init; } = new() { Enabled = true, Temperature = 0.2 };

    /// <summary>F8 — Natural-language Access Search. Constrained NL→query planning at temperature 0; off by default until schema review.</summary>
    public AiFeatureSetting AccessSearch { get; init; } = new() { Enabled = false, Temperature = 0 };

    /// <summary>F7 — Separation-of-Duties / toxic-combination detection. Structured JSON draft at temperature 0; off by default.</summary>
    public AiFeatureSetting SodAnalysis { get; init; } = new() { Enabled = false, Temperature = 0 };

    /// <summary>F4 — Access Certification / Recertification Copilot. Narrative output at the advisory temperature; off by default until PII review.</summary>
    public AiFeatureSetting AccessCertification { get; init; } = new() { Enabled = false, Temperature = 0.2 };

    /// <summary>F9 — Audit Narrative / Compliance-evidence generator. Narrative output at the advisory temperature; off by default until PII review.</summary>
    public AiFeatureSetting AuditNarrative { get; init; } = new() { Enabled = false, Temperature = 0.2 };
}

/// <summary>
/// Per-feature configuration: whether the feature is enabled and the sampling temperature applied
/// to its model calls. Structured-extraction features (policy, SoD, access-search planning) should
/// use <c>0</c> for determinism; narrative/summary features use a small positive value (e.g. 0.2)
/// for phrasing variety.
/// </summary>
public sealed class AiFeatureSetting
{
    public bool Enabled { get; init; }

    /// <summary>Sampling temperature (0.0–2.0) applied to this feature's model calls.</summary>
    public double Temperature { get; init; }
}

public sealed class AiLimitsOptions
{
    /// <summary>Maximum characters accepted for the caller's free-text prompt (instruction/question).
    /// A guard on user input, not a cost driver — grounding facts are added server-side. ~1k tokens.</summary>
    public int MaxPromptChars { get; init; } = 4000;

    public int RequestTimeoutSeconds { get; init; } = 30;

    /// <summary>Maximum completion tokens. Sized for the richest output (the Config Advisor's
    /// multi-section Markdown review and per-finding fixes); most features use far fewer. It is a
    /// ceiling, not a target, so raising it does not increase cost for shorter answers.</summary>
    public int MaxTokens { get; init; } = 2048;

    /// <summary>Fallback sampling temperature (0.0–2.0) used only when a feature does not specify its
    /// own <see cref="AiFeatureSetting.Temperature"/>. Each AI feature now carries its own temperature
    /// (see <see cref="AiFeatureOptions"/>): structured-extraction features run at 0 for determinism,
    /// while narrative/summary features use a small positive value. Exposed on the AI Usage and
    /// Overview pages so it can be tuned via configuration alone.</summary>
    public double Temperature { get; init; } = 0.2;

    /// <summary>Transient (429/503) retry attempts before giving up. Retry-After is honored when present.</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Upper bound (seconds) on any single retry wait, clamping large Retry-After values.</summary>
    public int MaxRetryDelaySeconds { get; init; } = 8;
}

public sealed class AiLoggingOptions
{
    /// <summary>
    /// When <c>true</c> (default) the free-text AI features (access search, SoD rule draft, policy
    /// authoring) persist the submitted prompt, its terminal outcome, and the model's interpretation
    /// to <c>authz.ai_prompt_logs</c> so admins can review failed prompts and improve the underlying
    /// logic. Unlike the metadata-only usage log this stores prompt text; reads are always
    /// admin-gated. Set to <c>false</c> to keep the metadata-only privacy boundary.
    /// </summary>
    public bool CapturePrompts { get; init; } = true;
}
