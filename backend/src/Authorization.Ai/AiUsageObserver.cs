namespace Authorization.Ai;

/// <summary>
/// Token usage reported by a single model call. Provider-neutral; the transport populates it from
/// the OpenAI-compatible <c>usage</c> block when the provider returns one.
/// </summary>
public readonly record struct AiTokenUsage(int PromptTokens, int CompletionTokens, int TotalTokens);

/// <summary>
/// Optional sink the chat transport writes token usage to after each successful call. The AI
/// assembly only *reports* usage; how it is aggregated or persisted is the host's concern, keeping
/// this assembly free of any storage or request-context dependency. When no observer is registered
/// (e.g. the deterministic Fake provider) usage is simply discarded.
/// </summary>
public interface IAiUsageObserver
{
    void Record(AiTokenUsage usage);
}
