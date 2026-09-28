using Authorization.Api.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authorization.Api.Tests.Observability;

public sealed class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_UsesInboundCorrelationIdForTraceIdentifierAndResponseHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "correlation-test";
        context.Response.Body = new MemoryStream();
        var middleware = new CorrelationIdMiddleware(
            _ => Task.CompletedTask,
            NullLogger<CorrelationIdMiddleware>.Instance);

        await middleware.InvokeAsync(context);
        await context.Response.StartAsync();

        Assert.Equal("correlation-test", context.TraceIdentifier);
        Assert.Equal("correlation-test", context.Response.Headers[CorrelationIdMiddleware.HeaderName]);
    }
}