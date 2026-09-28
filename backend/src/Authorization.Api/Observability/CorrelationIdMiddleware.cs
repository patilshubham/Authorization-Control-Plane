namespace Authorization.Api.Observability;

public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";

    /// <summary>
    /// Maximum length accepted for an inbound correlation id. Values longer than this are
    /// rejected in favor of the server-generated trace identifier to bound log/header size.
    /// </summary>
    internal const int MaxCorrelationIdLength = 128;

    private readonly RequestDelegate next;
    private readonly ILogger<CorrelationIdMiddleware> logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        this.next = next;
        this.logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string correlationId = ResolveCorrelationId(context);
        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
        });

        await next(context);
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        string? headerValue = context.Request.Headers[HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return context.TraceIdentifier;
        }

        string trimmed = headerValue.Trim();
        return IsAcceptable(trimmed) ? trimmed : context.TraceIdentifier;
    }

    private static bool IsAcceptable(string value)
    {
        if (value.Length > MaxCorrelationIdLength)
        {
            return false;
        }

        foreach (char character in value)
        {
            bool allowed = char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or ':';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}