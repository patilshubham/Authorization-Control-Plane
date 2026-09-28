using Authorization.Api.Observability;
namespace Authorization.Api.Tests.Observability;

public sealed class HealthEndpointTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public HealthEndpointTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoint_ReturnsHealthyWithCorrelationHeader(string path)
    {
        HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "health-test");

        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Equal("Healthy", body);
        Assert.True(response.Headers.TryGetValues(CorrelationIdMiddleware.HeaderName, out IEnumerable<string>? values));
        Assert.Contains("health-test", values);
    }
}