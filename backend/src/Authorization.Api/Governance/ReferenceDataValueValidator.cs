using System.Text.Json;

namespace Authorization.Api.Governance;

/// <summary>
/// Validates a reference-data document's value. The value must be well-formed JSON — typically an
/// array (e.g. <c>["US","CA"]</c>) or object — so policy conditions can resolve <c>reference.&lt;key&gt;</c>
/// deterministically. Pure logic with no persistence dependency so it can be unit tested in isolation.
/// </summary>
public static class ReferenceDataValueValidator
{
    /// <summary>
    /// Validates the supplied reference-data value JSON.
    /// </summary>
    /// <returns>An error message when invalid; otherwise <c>null</c>.</returns>
    public static string? Validate(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
        {
            return "Reference data value must not be empty.";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(valueJson);
            return document.RootElement.ValueKind is JsonValueKind.Array or JsonValueKind.Object
                ? null
                : "Reference data value must be a JSON array or object.";
        }
        catch (JsonException)
        {
            return "Reference data value must be valid JSON.";
        }
    }
}
