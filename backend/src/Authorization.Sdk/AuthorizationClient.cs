using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authorization.Sdk;

public interface IAuthorizationClient
{
    Task<AuthorizeResponse> AuthorizeAsync(AuthorizeRequest request, CancellationToken cancellationToken = default);

    Task<BatchAuthorizeResponse> AuthorizeBatchAsync(BatchAuthorizeRequest request, CancellationToken cancellationToken = default);
}

public sealed class AuthorizationClient : IAuthorizationClient
{
    public const string CorrelationIdHeaderName = "X-Correlation-ID";

    /// <summary>
    /// Maximum number of checks accepted in a single batch authorization request. Mirrors the
    /// server-side limit; larger batches are rejected client-side to avoid a wasted round trip.
    /// </summary>
    public const int MaxBatchSize = 50;

    private readonly HttpClient httpClient;
    private readonly AuthorizationClientOptions options;
    private readonly ILogger<AuthorizationClient> logger;

    public AuthorizationClient(
        HttpClient httpClient,
        AuthorizationClientOptions options,
        ILogger<AuthorizationClient>? logger = null)
    {
        this.httpClient = httpClient;
        this.options = options;
        this.logger = logger ?? NullLogger<AuthorizationClient>.Instance;
    }

    public async Task<AuthorizeResponse> AuthorizeAsync(AuthorizeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using HttpResponseMessage response = await SendWithResilienceAsync(
            () => BuildRequestAsync(HttpMethod.Post, "v1/authorize", request.CorrelationId, request, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        AuthorizeResponse decision = await ReadResponseAsync<AuthorizeResponse>(response, cancellationToken).ConfigureAwait(false);
        LogDecision(decision, request.CorrelationId);
        return decision;
    }

    public async Task<BatchAuthorizeResponse> AuthorizeBatchAsync(BatchAuthorizeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Checks is null || request.Checks.Count == 0)
        {
            throw new ArgumentException("A batch authorization request must contain at least one check.", nameof(request));
        }

        if (request.Checks.Count > MaxBatchSize)
        {
            throw new ArgumentException($"Batch requests are limited to {MaxBatchSize} checks.", nameof(request));
        }

        using HttpResponseMessage response = await SendWithResilienceAsync(
            () => BuildRequestAsync(HttpMethod.Post, "v1/authorize/batch", request.CorrelationId, request, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        BatchAuthorizeResponse decisions = await ReadResponseAsync<BatchAuthorizeResponse>(response, cancellationToken).ConfigureAwait(false);
        foreach (AuthorizeResponse decision in decisions.Results)
        {
            LogDecision(decision, request.CorrelationId);
        }

        return decisions;
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(HttpMethod method, string path, string? correlationId, object payload, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = await CreateRequestAsync(method, path, correlationId, cancellationToken).ConfigureAwait(false);
        request.Content = JsonContent.Create(payload, payload.GetType());
        return request;
    }

    /// <summary>
    /// Sends the request produced by <paramref name="requestFactory"/>, applying the configured
    /// per-request timeout and retry policy. A fresh <see cref="HttpRequestMessage"/> is created for
    /// each attempt because a request message cannot be re-sent. Retries apply only to transient
    /// transport failures and 5xx/408/429 responses; authorization decisions are read-only, so
    /// retrying is safe. Between attempts the client honors the server's <c>Retry-After</c> hint when
    /// present, otherwise a linearly increasing backoff with jitter to avoid synchronized retries.
    /// Defaults leave behavior unchanged (no extra timeout, no retries).
    /// </summary>
    private async Task<HttpResponseMessage> SendWithResilienceAsync(
        Func<Task<HttpRequestMessage>> requestFactory,
        CancellationToken cancellationToken)
    {
        int maxAttempts = Math.Max(1, options.MaxRetryAttempts + 1);
        HttpResponseMessage? response = null;

        for (int attempt = 1; ; attempt++)
        {
            HttpRequestMessage request = await requestFactory().ConfigureAwait(false);
            using CancellationTokenSource? timeoutCts = CreateTimeoutSource(cancellationToken, out CancellationToken effectiveToken);
            TimeSpan retryDelay;
            try
            {
                response?.Dispose();
                response = await httpClient.SendAsync(request, effectiveToken).ConfigureAwait(false);

                if (attempt >= maxAttempts || !IsTransient(response.StatusCode))
                {
                    return response;
                }

                retryDelay = ComputeRetryDelay(response, attempt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                request.Dispose();
                throw;
            }
            catch (Exception exception) when (attempt < maxAttempts && (exception is HttpRequestException || exception is OperationCanceledException))
            {
                logger.LogWarning(exception, "Authorization API request attempt {Attempt} of {MaxAttempts} failed; retrying.", attempt, maxAttempts);
                retryDelay = ComputeRetryDelay(null, attempt);
            }
            finally
            {
                request.Dispose();
            }

            await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Computes the delay before the next retry attempt. When the server supplied a <c>Retry-After</c>
    /// header (delta-seconds or an HTTP date) that value is honored. Otherwise a linear backoff of
    /// <see cref="AuthorizationClientOptions.RetryBaseDelay"/> × <paramref name="attempt"/> is used,
    /// with added jitter in the range [0, base) to spread out concurrent callers.
    /// </summary>
    private TimeSpan ComputeRetryDelay(HttpResponseMessage? response, int attempt)
    {
        if (response?.Headers.RetryAfter is { } retryAfter)
        {
            TimeSpan? serverDelay = retryAfter.Delta
                ?? (retryAfter.Date is { } date ? date - DateTimeOffset.UtcNow : null);
            if (serverDelay is { } delay && delay > TimeSpan.Zero)
            {
                return delay;
            }
        }

        TimeSpan baseDelay = options.RetryBaseDelay * attempt;
        if (baseDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        double jitterMs = Random.Shared.NextDouble() * baseDelay.TotalMilliseconds;
        return baseDelay + TimeSpan.FromMilliseconds(jitterMs);
    }

    private CancellationTokenSource? CreateTimeoutSource(CancellationToken cancellationToken, out CancellationToken effectiveToken)
    {
        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            effectiveToken = cancellationToken;
            return null;
        }

        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.RequestTimeout);
        effectiveToken = timeoutCts.Token;
        return timeoutCts;
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        (int)statusCode >= 500
        || statusCode == HttpStatusCode.RequestTimeout
        || statusCode == HttpStatusCode.TooManyRequests;

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string path, string? correlationId, CancellationToken cancellationToken)
    {
        string accessToken = await options.AccessTokenProvider(cancellationToken).ConfigureAwait(false);
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            request.Headers.Add(CorrelationIdHeaderName, correlationId);
        }

        return request;
    }

    private async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            T? body = await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false);
            return body ?? throw new AuthorizationApiException(
                response.StatusCode,
                "EMPTY_RESPONSE",
                "The authorization API returned an empty response.",
                TryGetCorrelationId(response));
        }

        ApiErrorEnvelope? envelope = await TryReadErrorEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
        string code = envelope?.Error.Code ?? "HTTP_ERROR";
        string message = envelope?.Error.Message ?? $"Authorization API returned {(int)response.StatusCode}.";
        string? correlationId = envelope?.CorrelationId ?? TryGetCorrelationId(response);
        throw new AuthorizationApiException(response.StatusCode, code, message, correlationId);
    }

    /// <summary>
    /// Attempts to deserialize the canonical error envelope from a failure response, returning
    /// <see langword="null"/> when the body is missing or not JSON (e.g. an HTML gateway error) so the
    /// caller can surface a generic <see cref="AuthorizationApiException"/> rather than a raw
    /// deserialization exception.
    /// </summary>
    private static async Task<ApiErrorEnvelope?> TryReadErrorEnvelopeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string? TryGetCorrelationId(HttpResponseMessage response) =>
        response.Headers.TryGetValues(CorrelationIdHeaderName, out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : null;

    private void LogDecision(AuthorizeResponse decision, string? correlationId)
    {
        logger.LogInformation(
            "Authorization decision {DecisionId} allowed={Allowed} denyReason={DenyReason} correlationId={CorrelationId}",
            decision.DecisionId,
            decision.Allowed,
            decision.DenyReason,
            correlationId);
    }
}

public sealed class AuthorizationClientOptions
{
    public AuthorizationClientOptions(Func<CancellationToken, Task<string>> accessTokenProvider)
    {
        AccessTokenProvider = accessTokenProvider;
    }

    public AuthorizationClientOptions(string accessToken)
        : this(_ => Task.FromResult(accessToken))
    {
    }

    /// <summary>
    /// Supplies the OAuth2 bearer access token the caller obtained from its identity provider
    /// (e.g. a client-credentials grant). The token's <c>azp</c>/audience bind the caller to an
    /// application; the authorization API validates it against that application's OIDC provider.
    /// </summary>
    public Func<CancellationToken, Task<string>> AccessTokenProvider { get; }

    /// <summary>
    /// Optional per-request timeout. When zero or negative (the default), no additional timeout is
    /// applied and the underlying <see cref="HttpClient.Timeout"/> governs the request.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Number of additional attempts made after the first attempt for transient transport failures
    /// or 5xx/408/429 responses. Defaults to 0 (no retries), preserving single-attempt behavior.
    /// </summary>
    public int MaxRetryAttempts { get; init; }

    /// <summary>
    /// Base backoff delay between retry attempts; the delay grows linearly with the attempt number.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);
}

public sealed record AuthorizeRequest(
    string ApplicationId,
    Subject Subject,
    IReadOnlyDictionary<string, object?>? Claims,
    Resource Resource,
    string Action,
    IReadOnlyDictionary<string, object?>? Context,
    [property: JsonIgnore] string? CorrelationId = null);

public sealed record BatchAuthorizeRequest(
    string ApplicationId,
    Subject Subject,
    IReadOnlyDictionary<string, object?>? Claims,
    IReadOnlyDictionary<string, object?>? Context,
    IReadOnlyList<BatchAuthorizeCheck> Checks,
    [property: JsonIgnore] string? CorrelationId = null);

public sealed record BatchAuthorizeCheck(
    Resource Resource,
    string Action,
    IReadOnlyDictionary<string, object?>? Context);

public sealed record Subject(string Type, string? Email);

public sealed record Resource(string Type, string? Id);

public sealed record AuthorizeResponse(
    bool Allowed,
    string DecisionId,
    string? DenyReason,
    AuthorizeReason Reason,
    IReadOnlyCollection<AuthorizeObligation> Obligations);

public sealed record AuthorizeReason(
    IReadOnlyCollection<string> MatchedRoles,
    IReadOnlyCollection<string> MatchedPermissions,
    IReadOnlyCollection<string> MatchedPolicies);

/// <summary>An advisory instruction the caller must enforce (e.g. <c>require_mfa</c>).</summary>
public sealed record AuthorizeObligation(string Id, string? Value);

public sealed record BatchAuthorizeResponse(IReadOnlyCollection<AuthorizeResponse> Results);

public sealed class AuthorizationApiException : Exception
{
    public AuthorizationApiException(HttpStatusCode statusCode, string errorCode, string message, string? correlationId = null)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        CorrelationId = correlationId;
    }

    public HttpStatusCode StatusCode { get; }

    public string ErrorCode { get; }

    /// <summary>
    /// Correlation id for the failed call, taken from the error envelope or the
    /// <c>X-Correlation-ID</c> response header when available, to cross-reference server logs.
    /// </summary>
    public string? CorrelationId { get; }
}

internal sealed record ApiErrorEnvelope(ApiError Error, string CorrelationId);

internal sealed record ApiError(string Code, string Message);
