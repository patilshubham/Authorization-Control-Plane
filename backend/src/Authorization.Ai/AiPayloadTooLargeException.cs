namespace Authorization.Ai;

/// <summary>
/// Raised when the AI provider rejects a request because its payload is too large (HTTP 413).
/// This is non-retryable at the transport level: the caller must rebuild a smaller prompt (for
/// example by dropping the grounding vocabulary) and try again. It lets the planner degrade
/// gracefully instead of surfacing a hard failure to the user.
/// </summary>
public sealed class AiPayloadTooLargeException : Exception
{
    public AiPayloadTooLargeException(string message)
        : base(message)
    {
    }

    public AiPayloadTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
