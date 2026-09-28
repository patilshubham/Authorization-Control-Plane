using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authorization.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace Authorization.Sdk.Tests;

public sealed class AuthorizationClientTests
{
    [Fact]
    public async Task AuthorizeAsync_SendsBearerTokenAndCorrelationId()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent(new
            {
                allowed = true,
                decisionId = "decision-1",
                denyReason = (string?)null,
                reason = new { matchedRoles = new[] { "invoice-approver" }, matchedPermissions = new[] { "invoice.approve" }, matchedPolicies = Array.Empty<string>() },
            }),
        });
        var client = CreateClient(handler);

        AuthorizeResponse response = await client.AuthorizeAsync(new AuthorizeRequest(
            "finance-app",
            new Subject("USER", "bob@local.test"),
            null,
            new Resource("invoice", "invoice-1"),
            "approve",
            new Dictionary<string, object?> { ["amount"] = 100 },
            CorrelationId: "corr-1"));

        Assert.True(response.Allowed);
        Assert.Equal("decision-1", response.DecisionId);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("/v1/authorize", handler.LastRequest.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-access-token", handler.LastRequest.Headers.Authorization?.Parameter);
        Assert.Contains("corr-1", handler.LastRequest.Headers.GetValues(AuthorizationClient.CorrelationIdHeaderName));
    }

    [Fact]
    public async Task AuthorizeBatchAsync_ReturnsOrderedResults()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent(new
            {
                results = new object[]
                {
                    new { allowed = true, decisionId = "decision-1", denyReason = (string?)null, reason = new { matchedRoles = Array.Empty<string>(), matchedPermissions = Array.Empty<string>(), matchedPolicies = Array.Empty<string>() } },
                    new { allowed = false, decisionId = "decision-2", denyReason = "EXPLICIT_DENY", reason = new { matchedRoles = Array.Empty<string>(), matchedPermissions = Array.Empty<string>(), matchedPolicies = Array.Empty<string>() } },
                },
            }),
        });
        var client = CreateClient(handler);

        BatchAuthorizeResponse response = await client.AuthorizeBatchAsync(new BatchAuthorizeRequest(
            "finance-app",
            new Subject("USER", "bob@local.test"),
            null,
            null,
            new[]
            {
                new BatchAuthorizeCheck(new Resource("invoice", "invoice-1"), "approve", new Dictionary<string, object?> { ["amount"] = 100 }),
                new BatchAuthorizeCheck(new Resource("invoice", "invoice-2"), "approve", new Dictionary<string, object?> { ["vendorRisk"] = "HIGH" }),
            },
            CorrelationId: "corr-2"));

        AuthorizeResponse[] results = response.Results.ToArray();
        Assert.Equal("decision-1", results[0].DecisionId);
        Assert.Equal("decision-2", results[1].DecisionId);
        Assert.Equal("/v1/authorize/batch", handler.LastRequest?.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task AuthorizeAsync_ThrowsCanonicalApiExceptionOnErrorEnvelope()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = JsonContent(new
            {
                error = new { code = "CALLER_APPLICATION_MISMATCH", message = "Runtime caller credentials are invalid for the requested application." },
                correlationId = "corr-3",
            }),
        });
        var client = CreateClient(handler);

        AuthorizationApiException exception = await Assert.ThrowsAsync<AuthorizationApiException>(() => client.AuthorizeAsync(new AuthorizeRequest(
            "crm-app",
            new Subject("USER", "alice@local.test"),
            null,
            new Resource("customer", "customer-1"),
            "update",
            null)));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal("CALLER_APPLICATION_MISMATCH", exception.ErrorCode);
    }

    [Fact]
    public async Task AuthorizeAsync_RetriesTransientFailure_ThenSucceeds()
    {
        var handler = new ScriptedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => OkDecision());
        var options = new AuthorizationClientOptions("test-access-token")
        {
            MaxRetryAttempts = 2,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        };
        var client = CreateClient(handler, options);

        AuthorizeResponse response = await client.AuthorizeAsync(SampleRequest());

        Assert.True(response.Allowed);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task AuthorizeAsync_ThrowsAfterRetriesExhausted()
    {
        var handler = new ScriptedHandler(
            _ => ServiceUnavailable(),
            _ => ServiceUnavailable(),
            _ => ServiceUnavailable());
        var options = new AuthorizationClientOptions("test-access-token")
        {
            MaxRetryAttempts = 2,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        };
        var client = CreateClient(handler, options);

        AuthorizationApiException exception = await Assert.ThrowsAsync<AuthorizationApiException>(
            () => client.AuthorizeAsync(SampleRequest()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task AuthorizeAsync_HonorsRetryAfterHeader()
    {
        var handler = new ScriptedHandler(
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(200));
                return response;
            },
            _ => OkDecision());
        var options = new AuthorizationClientOptions("test-access-token")
        {
            MaxRetryAttempts = 1,
            RetryBaseDelay = TimeSpan.Zero,
        };
        var client = CreateClient(handler, options);

        var stopwatch = Stopwatch.StartNew();
        AuthorizeResponse response = await client.AuthorizeAsync(SampleRequest());
        stopwatch.Stop();

        Assert.True(response.Allowed);
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 150,
            $"Expected the client to wait for the Retry-After hint; waited {stopwatch.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task AuthorizeAsync_AppliesPerRequestTimeout()
    {
        var handler = new DelayHandler(TimeSpan.FromSeconds(2));
        var options = new AuthorizationClientOptions("test-access-token")
        {
            RequestTimeout = TimeSpan.FromMilliseconds(50),
        };
        var client = CreateClient(handler, options);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AuthorizeAsync(SampleRequest()));
    }

    [Fact]
    public async Task AuthorizeAsync_ThrowsEmptyResponse_WhenBodyIsNull()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json"),
        });
        var client = CreateClient(handler);

        AuthorizationApiException exception = await Assert.ThrowsAsync<AuthorizationApiException>(
            () => client.AuthorizeAsync(SampleRequest()));

        Assert.Equal("EMPTY_RESPONSE", exception.ErrorCode);
    }

    [Fact]
    public async Task AuthorizeAsync_WrapsNonJsonErrorBody()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>502 Bad Gateway</html>", Encoding.UTF8, "text/html"),
        });
        var client = CreateClient(handler);

        AuthorizationApiException exception = await Assert.ThrowsAsync<AuthorizationApiException>(
            () => client.AuthorizeAsync(SampleRequest()));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Equal("HTTP_ERROR", exception.ErrorCode);
    }

    [Fact]
    public async Task AuthorizeAsync_PopulatesCorrelationIdFromEnvelope()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = JsonContent(new { error = new { code = "DENIED", message = "no" }, correlationId = "corr-envelope" }),
        });
        var client = CreateClient(handler);

        AuthorizationApiException exception = await Assert.ThrowsAsync<AuthorizationApiException>(
            () => client.AuthorizeAsync(SampleRequest()));

        Assert.Equal("corr-envelope", exception.CorrelationId);
    }

    [Fact]
    public async Task AuthorizeAsync_PopulatesCorrelationIdFromResponseHeader()
    {
        var handler = new ScriptedHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("<html>oops</html>", Encoding.UTF8, "text/html"),
            };
            response.Headers.Add(AuthorizationClient.CorrelationIdHeaderName, "corr-header");
            return response;
        });
        var client = CreateClient(handler);

        AuthorizationApiException exception = await Assert.ThrowsAsync<AuthorizationApiException>(
            () => client.AuthorizeAsync(SampleRequest()));

        Assert.Equal("corr-header", exception.CorrelationId);
    }

    [Fact]
    public async Task AuthorizeAsync_RequestsFreshTokenPerAttempt()
    {
        int tokenCalls = 0;
        var handler = new ScriptedHandler(
            _ => ServiceUnavailable(),
            _ => OkDecision());
        var options = new AuthorizationClientOptions(_ =>
        {
            Interlocked.Increment(ref tokenCalls);
            return Task.FromResult("token");
        })
        {
            MaxRetryAttempts = 1,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        };
        var client = CreateClient(handler, options);

        await client.AuthorizeAsync(SampleRequest());

        Assert.Equal(2, tokenCalls);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task AuthorizeAsync_SendsJsonAcceptHeader()
    {
        var handler = new ScriptedHandler(_ => OkDecision());
        var client = CreateClient(handler);

        await client.AuthorizeAsync(SampleRequest());

        Assert.Contains(handler.Requests[0].Headers.Accept, header => header.MediaType == "application/json");
    }

    [Fact]
    public async Task AuthorizeBatchAsync_Throws_WhenNoChecks()
    {
        var client = CreateClient(new ScriptedHandler(_ => OkDecision()));

        await Assert.ThrowsAsync<ArgumentException>(() => client.AuthorizeBatchAsync(new BatchAuthorizeRequest(
            "finance-app",
            new Subject("USER", "bob@local.test"),
            null,
            null,
            Array.Empty<BatchAuthorizeCheck>())));
    }

    [Fact]
    public async Task AuthorizeBatchAsync_Throws_WhenExceedingMaxBatchSize()
    {
        BatchAuthorizeCheck[] checks = Enumerable.Range(0, AuthorizationClient.MaxBatchSize + 1)
            .Select(i => new BatchAuthorizeCheck(new Resource("invoice", $"invoice-{i}"), "approve", null))
            .ToArray();
        var client = CreateClient(new ScriptedHandler(_ => OkDecision()));

        await Assert.ThrowsAsync<ArgumentException>(() => client.AuthorizeBatchAsync(new BatchAuthorizeRequest(
            "finance-app",
            new Subject("USER", "bob@local.test"),
            null,
            null,
            checks)));
    }

    [Fact]
    public void AddAuthorizationClient_RegistersResolvableClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationClient(
            new Uri("http://authorization.local/"),
            new AuthorizationClientOptions("test-access-token"));

        using ServiceProvider provider = services.BuildServiceProvider();
        var client = provider.GetService<IAuthorizationClient>();

        Assert.NotNull(client);
        Assert.IsType<AuthorizationClient>(client);
    }

    private static AuthorizationClient CreateClient(HttpMessageHandler handler, AuthorizationClientOptions? options = null)
    {
        return new AuthorizationClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://authorization.local/") },
            options ?? new AuthorizationClientOptions("test-access-token"));
    }

    private static HttpResponseMessage OkDecision() => new(HttpStatusCode.OK)
    {
        Content = JsonContent(new
        {
            allowed = true,
            decisionId = "decision-ok",
            denyReason = (string?)null,
            reason = new { matchedRoles = Array.Empty<string>(), matchedPermissions = Array.Empty<string>(), matchedPolicies = Array.Empty<string>() },
        }),
    };

    private static HttpResponseMessage ServiceUnavailable() => new(HttpStatusCode.ServiceUnavailable)
    {
        Content = JsonContent(new { error = new { code = "UNAVAILABLE", message = "temporary" }, correlationId = (string?)null }),
    };

    private static AuthorizeRequest SampleRequest() => new(
        "finance-app",
        new Subject("USER", "bob@local.test"),
        null,
        new Resource("invoice", "invoice-1"),
        "approve",
        null);

    private static JsonContent JsonContent(object value)
    {
        return System.Net.Http.Json.JsonContent.Create(value, options: new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage response;

        public CapturingHandler(HttpResponseMessage response)
        {
            this.response = response;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> steps;

        public ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps)
        {
            this.steps = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(steps);
        }

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Func<HttpRequestMessage, HttpResponseMessage> step = steps.Count > 1 ? steps.Dequeue() : steps.Peek();
            return Task.FromResult(step(request));
        }
    }

    private sealed class DelayHandler : HttpMessageHandler
    {
        private readonly TimeSpan delay;

        public DelayHandler(TimeSpan delay)
        {
            this.delay = delay;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return OkDecision();
        }
    }
}
