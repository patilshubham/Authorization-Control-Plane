using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authorization.Ai;

/// <summary>
/// Logs the effective AI feature state once at startup so operators can confirm
/// whether AI is enabled and, if it was requested but disabled, why. Never logs secrets.
/// </summary>
internal sealed class AiStartupLogger : IHostedService
{
    private readonly ILogger logger;
    private readonly AiAvailability availability;
    private readonly IReadOnlyList<string> configErrors;

    public AiStartupLogger(ILogger logger, AiAvailability availability, IReadOnlyList<string> configErrors)
    {
        this.logger = logger;
        this.availability = availability;
        this.configErrors = configErrors;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (availability.Enabled)
        {
            logger.LogInformation(
                "AI assistance enabled (provider: {Provider}; policyAuthoring: {PolicyAuthoring}; decisionExplainer: {DecisionExplainer}; impactAnalysis: {ImpactAnalysis}; configAdvisor: {ConfigAdvisor}; accessSearch: {AccessSearch}; sodAnalysis: {SodAnalysis}; accessCertification: {AccessCertification}; auditNarrative: {AuditNarrative}).",
                availability.Provider,
                availability.Features.PolicyAuthoring,
                availability.Features.DecisionExplainer,
                availability.Features.ImpactAnalysis,
                availability.Features.ConfigAdvisor,
                availability.Features.AccessSearch,
                availability.Features.SodAnalysis,
                availability.Features.AccessCertification,
                availability.Features.AuditNarrative);
        }
        else if (configErrors.Count > 0)
        {
            logger.LogWarning(
                "AI assistance was requested but is disabled due to configuration issues: {Issues}",
                string.Join("; ", configErrors));
        }
        else
        {
            logger.LogInformation("AI assistance disabled (Ai:Enabled is false).");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
