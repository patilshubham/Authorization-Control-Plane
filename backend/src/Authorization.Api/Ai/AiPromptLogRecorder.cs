using System.Text.Json;
using Authorization.Ai;
using Authorization.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authorization.Api.Ai;

/// <summary>Well-known terminal outcomes recorded for a free-text AI prompt.</summary>
public static class AiPromptOutcomes
{
    /// <summary>The request was planned/drafted and executed successfully.</summary>
    public const string Succeeded = "Succeeded";

    /// <summary>The model ran but produced nothing usable (e.g. an unmapped access-search entity).</summary>
    public const string Unmapped = "Unmapped";

    /// <summary>The model output was rejected by the closed schema (unknown field/operator/matcher).</summary>
    public const string ValidationFailed = "ValidationFailed";

    /// <summary>The submitted prompt itself was rejected before the model ran (e.g. too long).</summary>
    public const string InvalidInput = "InvalidInput";

    /// <summary>The model call exceeded the per-request timeout.</summary>
    public const string Timeout = "Timeout";

    /// <summary>The model call failed with an unexpected error.</summary>
    public const string ModelError = "ModelError";
}

/// <summary>Per-request context for a free-text AI prompt supplied by the calling controller.</summary>
public sealed record AiPromptLogContext(
    string Feature,
    string? ApplicationId,
    string? Actor,
    string? ActorRole,
    string PromptText,
    string? CorrelationId);

/// <summary>
/// Persists one <see cref="AiPromptLogEntity"/> per free-text AI request (access search, SoD rule
/// draft, policy authoring): the exact prompt, its terminal outcome — including the validation
/// failures the metadata-only <see cref="AiInvocationRecorder"/> cannot see — and the model's
/// interpretation. Unlike the usage log this deliberately stores prompt text so admins can review
/// failed prompts and improve the underlying logic (see D-0013). Capture is gated by
/// <c>Ai:Logging:CapturePrompts</c>; recording is best-effort and never changes the advisory result.
/// </summary>
public sealed class AiPromptLogRecorder
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly AuthorizationDbContext dbContext;
    private readonly AiAvailability availability;
    private readonly AiOptions options;
    private readonly ILogger<AiPromptLogRecorder> logger;

    public AiPromptLogRecorder(
        AuthorizationDbContext dbContext,
        AiAvailability availability,
        IOptions<AiOptions> options,
        ILogger<AiPromptLogRecorder> logger)
    {
        this.dbContext = dbContext;
        this.availability = availability;
        this.options = options.Value;
        this.logger = logger;
    }

    /// <summary>Whether prompt capture is enabled by configuration.</summary>
    public bool Enabled => options.Logging.CapturePrompts;

    /// <summary>
    /// Records a prompt-log row for the given context and terminal outcome. No-op when capture is
    /// disabled or the prompt is blank. Any persistence failure is swallowed with a warning so the
    /// advisory response is never affected.
    /// </summary>
    public async Task RecordAsync(
        AiPromptLogContext context,
        string outcome,
        string? errorMessage = null,
        object? interpretation = null)
    {
        if (!options.Logging.CapturePrompts || string.IsNullOrWhiteSpace(context.PromptText))
        {
            return;
        }

        try
        {
            bool live = !string.Equals(availability.Provider, AiProviders.Fake, StringComparison.OrdinalIgnoreCase);
            dbContext.AiPromptLogs.Add(new AiPromptLogEntity
            {
                Feature = context.Feature,
                ActorEmail = context.Actor,
                ActorRole = context.ActorRole,
                ApplicationId = context.ApplicationId,
                PromptText = Truncate(context.PromptText, options.Limits.MaxPromptChars),
                Outcome = outcome,
                ErrorMessage = errorMessage,
                Interpretation = Serialize(interpretation),
                Provider = availability.Provider,
                Model = live ? options.AzureOpenAI.ChatDeployment : null,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = context.CorrelationId,
            });

            // CancellationToken.None so the row still persists even when the request was aborted or
            // the AI call timed out.
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record AI prompt log for feature {Feature}.", context.Feature);
        }
    }

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];

    private static string? Serialize(object? interpretation)
    {
        if (interpretation is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Serialize(interpretation, SerializerOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
