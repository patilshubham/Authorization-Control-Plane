namespace Authorization.Ai;

/// <summary>
/// Immutable snapshot of the effective AI feature state, computed once at startup
/// after validating configuration. This is the single source of truth consulted by
/// the config endpoint and the AI controllers, so that a misconfigured provider
/// results in AI being reported (and treated) as disabled rather than crashing.
/// </summary>
public sealed class AiAvailability
{
    public AiAvailability(bool enabled, string provider, AiFeatureAvailability features)
    {
        Enabled = enabled;
        Provider = provider;
        Features = enabled ? features : AiFeatureAvailability.None;
    }

    public bool Enabled { get; }

    public string Provider { get; }

    public AiFeatureAvailability Features { get; }

    public static AiAvailability Disabled { get; } =
        new(false, AiProviders.Fake, AiFeatureAvailability.None);
}

/// <summary>Per-feature enablement, gated by both configuration and the master switch.</summary>
public sealed record AiFeatureAvailability(
    bool PolicyAuthoring,
    bool DecisionExplainer,
    bool ImpactAnalysis,
    bool ConfigAdvisor,
    bool AccessSearch,
    bool SodAnalysis,
    bool AccessCertification,
    bool AuditNarrative)
{
    public static AiFeatureAvailability None { get; } = new(false, false, false, false, false, false, false, false);
}
