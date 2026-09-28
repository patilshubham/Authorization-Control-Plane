using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Authorization.Ai.Providers;

/// <summary>
/// Minimal transport over an OpenAI-compatible chat/completions endpoint. Supports two
/// wire dialects selected by <see cref="AiOptions.Provider"/>:
/// <list type="bullet">
///   <item><description><c>OpenAI</c> — OpenAI-compatible endpoints (including GitHub Models):
///   <c>{Endpoint}/chat/completions</c> with a <c>Bearer</c> token and the model in the body.</description></item>
///   <item><description><c>AzureOpenAI</c> — Azure resource endpoints:
///   <c>{Endpoint}/openai/deployments/{deployment}/chat/completions?api-version=...</c> with an <c>api-key</c> header.</description></item>
/// </list>
/// This is a deliberately small, dependency-light client so the AI assembly does not take a
/// hard dependency on a preview SDK. The API key is never logged.
/// </summary>
internal interface IChatCompletionClient
{
    // temperature is optional; when null the client applies its default. Extraction/planning callers
    // pass 0 for maximum determinism; narrative callers leave it null.
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken, double? temperature = null);
}

internal sealed class HttpChatCompletionClient : IChatCompletionClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient http;
    private readonly AiOptions options;
    private readonly bool isAzure;
    private readonly IAiUsageObserver? usageObserver;

    public HttpChatCompletionClient(HttpClient http, AiOptions options, string provider, IAiUsageObserver? usageObserver = null)
    {
        this.http = http;
        this.options = options;
        isAzure = string.Equals(provider, AiProviders.AzureOpenAI, StringComparison.OrdinalIgnoreCase);
        this.usageObserver = usageObserver;
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken, double? temperature = null)
    {
        AzureOpenAiOptions chat = options.AzureOpenAI;

        // Reasoning-style deployments (e.g. gpt-5.5, o-series) reject a non-default temperature
        // and the legacy max_tokens field outright, so both are opt-out via configuration.
        double? effectiveTemperature = chat.SupportsTemperature
            ? temperature ?? options.Limits.Temperature
            : null;

        var payload = new ChatRequest(
            Model: isAzure ? null : chat.ChatDeployment,
            Messages:
            [
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", userPrompt),
            ],
            // Callers needing strict determinism (spec/JSON extraction) pass 0; narrative callers pass
            // null and inherit the configured default so it can be tuned from appsettings alone.
            Temperature: effectiveTemperature,
            MaxTokens: chat.SupportsMaxTokens ? options.Limits.MaxTokens : null,
            MaxCompletionTokens: chat.SupportsMaxTokens ? null : options.Limits.MaxTokens,
            ResponseFormat: new ResponseFormat("json_object"));

        string baseUrl = chat.Endpoint.TrimEnd('/');
        string requestUri = isAzure
            ? $"{baseUrl}/openai/deployments/{chat.ChatDeployment}/chat/completions?api-version={chat.ApiVersion}"
            : $"{baseUrl}/chat/completions";

        int maxRetries = Math.Max(0, options.Limits.MaxRetries);
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = JsonContent.Create(payload, options: SerializerOptions),
            };

            if (isAzure)
            {
                request.Headers.Add("api-key", chat.ApiKey);
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", chat.ApiKey);
            }

            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);

            // 413 cannot be resolved by retrying the same request — signal the caller to shrink the
            // prompt (drop vocabulary / detail) and try again with a smaller payload.
            if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                throw new AiPayloadTooLargeException("The AI request payload exceeded the provider's size limit.");
            }

            // Transient throttling / unavailability — honor Retry-After with bounded backoff.
            if ((response.StatusCode == HttpStatusCode.TooManyRequests
                    || response.StatusCode == HttpStatusCode.ServiceUnavailable)
                && attempt < maxRetries)
            {
                TimeSpan delay = ResolveRetryDelay(response, attempt);
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            response.EnsureSuccessStatusCode();

            ChatResponse? body = await response.Content.ReadFromJsonAsync<ChatResponse>(SerializerOptions, cancellationToken);

            // Report token usage (when the provider returns a usage block) so the host can aggregate cost.
            if (usageObserver is not null && body?.Usage is { } usage)
            {
                usageObserver.Record(new AiTokenUsage(
                    usage.PromptTokens ?? 0,
                    usage.CompletionTokens ?? 0,
                    usage.TotalTokens ?? 0));
            }

            return body?.Choices is { Count: > 0 } choices
                ? choices[0].Message?.Content ?? string.Empty
                : string.Empty;
        }
    }

    // Chooses how long to wait before a transient retry: prefer the provider's Retry-After header,
    // otherwise exponential backoff, always clamped to the configured ceiling.
    private TimeSpan ResolveRetryDelay(HttpResponseMessage response, int attempt)
    {
        TimeSpan cap = TimeSpan.FromSeconds(Math.Max(1, options.Limits.MaxRetryDelaySeconds));

        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
        if (retryAfter is not null)
        {
            if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
            {
                return delta > cap ? cap : delta;
            }

            if (retryAfter.Date is { } date)
            {
                TimeSpan until = date - DateTimeOffset.UtcNow;
                if (until > TimeSpan.Zero)
                {
                    return until > cap ? cap : until;
                }
            }
        }

        TimeSpan backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
        return backoff > cap ? cap : backoff;
    }

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("temperature")] double? Temperature,
        [property: JsonPropertyName("max_tokens")] int? MaxTokens,
        [property: JsonPropertyName("max_completion_tokens")] int? MaxCompletionTokens,
        [property: JsonPropertyName("response_format")] ResponseFormat? ResponseFormat);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ResponseFormat(
        [property: JsonPropertyName("type")] string Type);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice>? Choices,
        [property: JsonPropertyName("usage")] ChatUsage? Usage);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatResponseMessage? Message);

    private sealed record ChatResponseMessage(
        [property: JsonPropertyName("content")] string? Content);

    private sealed record ChatUsage(
        [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int? CompletionTokens,
        [property: JsonPropertyName("total_tokens")] int? TotalTokens);
}
