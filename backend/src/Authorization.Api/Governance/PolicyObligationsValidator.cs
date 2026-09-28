using System.Text.Json;

namespace Authorization.Api.Governance;

/// <summary>
/// Validates a policy's obligations document. Obligations are a JSON array whose entries are
/// either bare id strings (<c>"require_mfa"</c>) or objects (<c>{ "id": string, "value"?: string }</c>).
/// Pure logic with no persistence dependency so it can be unit tested in isolation.
/// </summary>
public static class PolicyObligationsValidator
{
    /// <summary>
    /// Validates the supplied obligations JSON document.
    /// </summary>
    /// <returns>An error message when invalid; otherwise <c>null</c>.</returns>
    public static string? Validate(string? obligationsJson)
    {
        if (string.IsNullOrWhiteSpace(obligationsJson))
        {
            // Treated as "no obligations"; the entity default is an empty array.
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(obligationsJson);
        }
        catch (JsonException)
        {
            return "Obligations must be valid JSON.";
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return "Obligations must be a JSON array.";
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                string? id;
                if (element.ValueKind == JsonValueKind.String)
                {
                    id = element.GetString();
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    if (!element.TryGetProperty("id", out JsonElement idElement) || idElement.ValueKind != JsonValueKind.String)
                    {
                        return "Each obligation object must include a string 'id'.";
                    }

                    if (element.TryGetProperty("value", out JsonElement valueElement)
                        && valueElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    {
                        return "Obligation 'value' must be a string when provided.";
                    }

                    id = idElement.GetString();
                }
                else
                {
                    return "Each obligation must be a string id or an object with an 'id'.";
                }

                if (string.IsNullOrWhiteSpace(id))
                {
                    return "Obligation id must not be empty.";
                }

                if (!seen.Add(id))
                {
                    return $"Duplicate obligation id '{id}'.";
                }
            }
        }

        return null;
    }
}
