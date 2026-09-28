using System.Net;
using System.Text.Json;
using Authorization.Api.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authorization.Api.Tests.Errors;

public sealed class ExceptionHandlingMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ReturnsCanonicalEnvelopeForUnhandledException()
    {
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "trace-exception",
        };
        context.Response.Body = new MemoryStream();
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("Sensitive implementation detail."),
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        ApiErrorEnvelope? envelope = await JsonSerializer.DeserializeAsync<ApiErrorEnvelope>(
            context.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal((int)HttpStatusCode.InternalServerError, context.Response.StatusCode);
        Assert.NotNull(envelope);
        Assert.Equal(ApiErrorFactory.InternalErrorCode, envelope.Error.Code);
        Assert.Equal("An unexpected error occurred.", envelope.Error.Message);
        Assert.Equal("trace-exception", envelope.CorrelationId);
        Assert.Null(envelope.Error.Details);
    }
}