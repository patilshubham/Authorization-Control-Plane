using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Authorization.Api.Contracts;
using Authorization.Api.Controllers;
using Authorization.Api.Errors;
using Authorization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Authorization.Api.Tests.RuntimeAuthorization;

public sealed class RuntimeAuthorizationControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public RuntimeAuthorizationControllerTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Authorize_ReturnsAllowDecisionForBoundRuntimeCaller()
    {
        await factory.SeedLocalDataAsync();
        HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = CreateRequest("/v1/authorize", new
        {
            applicationId = "pricing-management",
            subject = new { type = "USER", email = "user7.lead@icis.com" },
            resource = new { type = "price", id = "price-1" },
            action = "publish",
            context = new Dictionary<string, object?> { ["status"] = "READY_TO_PUBLISH" },
        }, factory.CreateUserRuntimeJwt("pricing-management-user-client", "user7.lead@icis.com"));

        using HttpResponseMessage response = await client.SendAsync(request);
        AuthorizeResponse? body = await response.Content.ReadFromJsonAsync<AuthorizeResponse>();

        response.EnsureSuccessStatusCode();
        Assert.NotNull(body);
        Assert.True(body.Allowed, body.DenyReason);
        Assert.False(string.IsNullOrWhiteSpace(body.DecisionId));
        Assert.Null(body.DenyReason);
        Assert.Contains("price.publish", body.Reason.MatchedPermissions);
        DecisionEntity persistedDecision = await WaitForDecisionAsync(body.DecisionId);
        Assert.True(persistedDecision.Allowed);
        Assert.Equal("pricing-management", persistedDecision.ApplicationId);
        Assert.Equal("user7.lead@icis.com", persistedDecision.SubjectEmail);
    }

    [Fact]
    public async Task Authorize_DerivesServiceSubjectForMachineToMachineCaller()
    {
        await factory.SeedLocalDataAsync();
        HttpClient client = factory.CreateClient();
        // A client-credentials token has no user identity; the subject is the calling service,
        // derived from the azp claim. The seeded service account holds pricing-lead (price.publish).
        using HttpRequestMessage request = CreateRequest("/v1/authorize", new
        {
            applicationId = "pricing-management",
            subject = new { type = "SERVICE_ACCOUNT" },
            resource = new { type = "price", id = "price-1" },
            action = "publish",
            context = new Dictionary<string, object?> { ["status"] = "READY_TO_PUBLISH" },
        }, factory.CreateRuntimeJwt("pricing-management-runtime-client"));

        using HttpResponseMessage response = await client.SendAsync(request);
        AuthorizeResponse? body = await response.Content.ReadFromJsonAsync<AuthorizeResponse>();

        response.EnsureSuccessStatusCode();
        Assert.NotNull(body);
        Assert.True(body.Allowed, body.DenyReason);
        DecisionEntity persistedDecision = await WaitForDecisionAsync(body.DecisionId);
        Assert.Equal("SERVICE_ACCOUNT", persistedDecision.SubjectType);
        Assert.Equal("pricing-management-runtime-client", persistedDecision.SubjectEmail);
    }

    [Fact]
    public async Task Authorize_ReturnsForbiddenWhenBodySubjectConflictsWithToken()
    {
        await factory.SeedLocalDataAsync();
        HttpClient client = factory.CreateClient();
        // The verified token identifies user7.lead; a request claiming to act as a different user
        // must be rejected rather than trusted.
        using HttpRequestMessage request = CreateRequest("/v1/authorize", new
        {
            applicationId = "pricing-management",
            subject = new { type = "USER", email = "someone.else@icis.com" },
            resource = new { type = "price", id = "price-1" },
            action = "publish",
            context = new Dictionary<string, object?> { ["status"] = "READY_TO_PUBLISH" },
        }, factory.CreateUserRuntimeJwt("pricing-management-user-client", "user7.lead@icis.com"));

        using HttpResponseMessage response = await client.SendAsync(request);
        ApiErrorEnvelope? body = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("SUBJECT_MISMATCH", body.Error.Code);
    }

    [Fact]
    public async Task Authorize_ReturnsForbiddenWhenSubjectClaimMissingFromToken()
    {
        await factory.SeedLocalDataAsync();
        HttpClient client = factory.CreateClient();
        // A token bound to the user client but lacking preferred_username is the wrong token type
        // for a USER provider; the subject cannot be derived, so it is refused.
        using HttpRequestMessage request = CreateRequest("/v1/authorize", new
        {
            applicationId = "pricing-management",
            subject = new { type = "USER" },
            resource = new { type = "price", id = "price-1" },
            action = "publish",
            context = new Dictionary<string, object?> { ["status"] = "READY_TO_PUBLISH" },
        }, factory.CreateRuntimeJwt("pricing-management-user-client"));

        using HttpResponseMessage response = await client.SendAsync(request);
        ApiErrorEnvelope? body = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("CALLER_SUBJECT_CLAIM_MISSING", body.Error.Code);
    }

    [Fact]
    public async Task Authorize_ReturnsForbiddenWhenCallerApplicationDoesNotMatchRequest()
    {
        await factory.SeedLocalDataAsync();
        HttpClient client = factory.CreateClient();
        // Token is authentic for the shared realm but its azp binds it to pricing-management,
        // so it must not be accepted when authorizing against market-reference.
        using HttpRequestMessage request = CreateRequest("/v1/authorize", new
        {
            applicationId = "market-reference",
            subject = new { type = "USER", email = "alice@local.test" },
            resource = new { type = "reference", id = "reference-1" },
            action = "update",
            context = new Dictionary<string, object?>(),
        }, factory.CreateRuntimeJwt("pricing-management-runtime-client"));

        using HttpResponseMessage response = await client.SendAsync(request);
        ApiErrorEnvelope? body = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("CALLER_APPLICATION_MISMATCH", body.Error.Code);
    }

    [Fact]
    public async Task Authorize_ReturnsUnauthorizedWithoutLeakingInvalidToken()
    {
        await factory.SeedLocalDataAsync();
        HttpClient client = factory.CreateClient();
        string untrustedToken = factory.CreateUntrustedRuntimeJwt("pricing-management-runtime-client");
        using HttpRequestMessage request = CreateRequest("/v1/authorize", new
        {
            applicationId = "pricing-management",
            subject = new { type = "USER", email = "bob@local.test" },
            resource = new { type = "price", id = "price-1" },
            action = "publish",
            context = new Dictionary<string, object?> { ["status"] = "READY_TO_PUBLISH" },
        }, untrustedToken);

        using HttpResponseMessage response = await client.SendAsync(request);
        string responseBody = await response.Content.ReadAsStringAsync();
        ApiErrorEnvelope? body = JsonSerializer.Deserialize<ApiErrorEnvelope>(responseBody, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("CALLER_UNAUTHENTICATED", body.Error.Code);
        Assert.DoesNotContain(untrustedToken, responseBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Authorize_ReturnsValidationErrorWhenContextIsTooLarge()
    {
        await factory.SeedLocalDataAsync();
        HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = CreateRequest("/v1/authorize", new
        {
            applicationId = "pricing-management",
            subject = new { type = "USER", email = "bob@local.test" },
            resource = new { type = "price", id = "price-1" },
            action = "publish",
            context = new Dictionary<string, object?> { ["payload"] = new string('x', RuntimeAuthorizationController.MaxContextJsonBytes + 1) },
        }, factory.CreateRuntimeJwt("pricing-management-runtime-client"));

        using HttpResponseMessage response = await client.SendAsync(request);
        ApiErrorEnvelope? body = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("CONTEXT_TOO_LARGE", body.Error.Code);
    }

    [Fact]
    public async Task AuthorizeBatch_ReturnsPayloadTooLargeWhenBatchLimitIsExceeded()
    {
        await factory.SeedLocalDataAsync();
        HttpClient client = factory.CreateClient();
        object[] checks = Enumerable.Range(0, RuntimeAuthorizationController.MaxBatchSize + 1)
            .Select(index => new
            {
                resource = new { type = "invoice", id = $"invoice-{index}" },
                action = "approve",
                context = new Dictionary<string, object?> { ["amount"] = 100 },
            })
            .ToArray();
        using HttpRequestMessage request = CreateRequest("/v1/authorize/batch", new
        {
            applicationId = "pricing-management",
            subject = new { type = "USER", email = "bob@local.test" },
            checks,
        }, factory.CreateRuntimeJwt("pricing-management-runtime-client"));

        using HttpResponseMessage response = await client.SendAsync(request);
        ApiErrorEnvelope? body = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("BATCH_TOO_LARGE", body.Error.Code);
    }

    [Fact]
    public async Task AuthorizeBatch_ReturnsMixedResultsInRequestOrder()
    {
        await factory.SeedLocalDataAsync();
        HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = CreateRequest("/v1/authorize/batch", new
        {
            applicationId = "pricing-management",
            subject = new { type = "USER", email = "user7.lead@icis.com" },
            checks = new object[]
            {
                new
                {
                    resource = new { type = "price", id = "price-1" },
                    action = "publish",
                    context = new Dictionary<string, object?> { ["status"] = "READY_TO_PUBLISH" },
                },
                new
                {
                    resource = new { type = "price", id = "price-2" },
                    action = "publish",
                    context = new Dictionary<string, object?> { ["isAuthor"] = "true" },
                },
            },
        }, factory.CreateUserRuntimeJwt("pricing-management-user-client", "user7.lead@icis.com"));

        using HttpResponseMessage response = await client.SendAsync(request);
        BatchAuthorizeResponse? body = await response.Content.ReadFromJsonAsync<BatchAuthorizeResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web));

        response.EnsureSuccessStatusCode();
        Assert.NotNull(body);
        AuthorizeResponse[] results = body.Results.ToArray();
        Assert.Equal(2, results.Length);
        Assert.True(results[0].Allowed, results[0].DenyReason);
        Assert.False(results[1].Allowed);
        Assert.Equal("EXPLICIT_DENY", results[1].DenyReason);
        Assert.All(results, result => Assert.False(string.IsNullOrWhiteSpace(result.DecisionId)));
        Assert.NotNull(await WaitForDecisionAsync(results[0].DecisionId));
        Assert.NotNull(await WaitForDecisionAsync(results[1].DecisionId));
    }

    private static HttpRequestMessage CreateRequest(string path, object body, string? bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return request;
    }

    private async Task<DecisionEntity> WaitForDecisionAsync(string decisionId)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            using IServiceScope scope = factory.Services.CreateScope();
            AuthorizationDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
            DecisionEntity? decision = await dbContext.Decisions.FirstOrDefaultAsync(entity => entity.DecisionId == decisionId);
            if (decision is not null)
            {
                return decision;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Decision {decisionId} was not persisted.");
    }
}
