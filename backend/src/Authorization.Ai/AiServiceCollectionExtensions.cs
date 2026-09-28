using System.Net.Http.Headers;
using Authorization.Ai.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authorization.Ai;

public static class AiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the opt-in AI assistance services. When the master switch is off,
    /// or when the selected provider is misconfigured, AI is registered as disabled:
    /// no <see cref="IAiAssistant"/> is resolvable and <see cref="AiAvailability"/>
    /// reports <c>Enabled = false</c>. This never throws — a misconfigured provider
    /// degrades gracefully instead of blocking application startup.
    /// </summary>
    public static IServiceCollection AddAuthorizationAi(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        AiOptions options = configuration.GetSection(AiOptions.SectionName).Get<AiOptions>() ?? new AiOptions();

        List<string> configErrors = [];
        AiAvailability availability = BuildAvailability(options, configErrors);

        services.AddSingleton(availability);
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));

        if (availability.Enabled)
        {
            RegisterAssistant(services, options);
        }

        services.AddSingleton<IHostedService>(sp => new AiStartupLogger(
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Authorization.Ai"),
            availability,
            configErrors));

        return services;
    }

    private static void RegisterAssistant(IServiceCollection services, AiOptions options)
    {
        // The deterministic stub keeps tests and offline development fully keyless.
        if (string.Equals(options.Provider, AiProviders.Fake, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IAiAssistant, FakeAiAssistant>();
            return;
        }

        // Live providers (OpenAI-compatible / GitHub Models / Azure OpenAI) share one small
        // HTTP transport. The request timeout is enforced by the caller's cancellation token.
        services.AddHttpClient(ChatClientAiAssistant.HttpClientName, http =>
        {
            http.Timeout = Timeout.InfiniteTimeSpan;
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        services.AddSingleton<IAiAssistant>(sp =>
        {
            HttpClient http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(ChatClientAiAssistant.HttpClientName);
            // The usage observer is optional: when the host registers one, token usage is reported for
            // cost accounting; otherwise usage is discarded and the client behaves exactly as before.
            IAiUsageObserver? usageObserver = sp.GetService<IAiUsageObserver>();
            var completionClient = new HttpChatCompletionClient(http, options, options.Provider, usageObserver);
            return new ChatClientAiAssistant(
                completionClient,
                options,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<ChatClientAiAssistant>());
        });
    }

    private static AiAvailability BuildAvailability(AiOptions options, List<string> configErrors)
    {
        if (!options.Enabled)
        {
            return AiAvailability.Disabled;
        }

        string provider = options.Provider;

        // The stub provider needs no external configuration.
        bool providerConfigured = provider switch
        {
            AiProviders.Fake => true,
            AiProviders.AzureOpenAI or AiProviders.OpenAI => ValidateChatProvider(options, configErrors),
            _ => Fail(configErrors, $"Unknown AI provider '{provider}'. Expected one of: {AiProviders.AzureOpenAI}, {AiProviders.OpenAI}, {AiProviders.Fake}."),
        };

        if (!providerConfigured)
        {
            return AiAvailability.Disabled;
        }

        var features = new AiFeatureAvailability(
            PolicyAuthoring: options.Features.PolicyAuthoring.Enabled,
            DecisionExplainer: options.Features.DecisionExplainer.Enabled,
            ImpactAnalysis: options.Features.ImpactAnalysis.Enabled,
            ConfigAdvisor: options.Features.ConfigAdvisor.Enabled,
            AccessSearch: options.Features.AccessSearch.Enabled,
            SodAnalysis: options.Features.SodAnalysis.Enabled,
            AccessCertification: options.Features.AccessCertification.Enabled,
            AuditNarrative: options.Features.AuditNarrative.Enabled);

        return new AiAvailability(true, provider, features);
    }

    private static bool ValidateChatProvider(AiOptions options, List<string> configErrors)
    {
        // Until the real client is wired the app still runs against the stub, but we
        // surface configuration gaps so an operator can see why AI is degraded.
        bool ok = true;
        if (string.IsNullOrWhiteSpace(options.AzureOpenAI.Endpoint))
        {
            ok = Fail(configErrors, "Ai:AzureOpenAI:Endpoint is required for the configured provider.");
        }

        if (string.IsNullOrWhiteSpace(options.AzureOpenAI.ApiKey))
        {
            ok = Fail(configErrors, "Ai:AzureOpenAI:ApiKey is required for the configured provider.");
        }

        if (string.IsNullOrWhiteSpace(options.AzureOpenAI.ChatDeployment))
        {
            ok = Fail(configErrors, "Ai:AzureOpenAI:ChatDeployment is required for the configured provider.");
        }

        return ok;
    }

    private static bool Fail(List<string> configErrors, string message)
    {
        configErrors.Add(message);
        return false;
    }
}
