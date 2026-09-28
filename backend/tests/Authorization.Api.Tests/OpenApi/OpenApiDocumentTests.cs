namespace Authorization.Api.Tests.OpenApi;

public sealed class OpenApiDocumentTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public OpenApiDocumentTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task OpenApiDocument_IncludesCanonicalErrorEnvelopeSchema()
    {
        HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        string document = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("ApiErrorEnvelope", document);
        Assert.Contains("correlationId", document);
        Assert.Contains("ApiErrorDetail", document);
        Assert.Contains("ApiError", document);
    }
}