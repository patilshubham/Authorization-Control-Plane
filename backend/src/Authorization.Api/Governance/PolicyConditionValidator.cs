using System.Text.Json;
using Authorization.Infrastructure.RuntimeAuthorization;

namespace Authorization.Api.Governance;

/// <summary>
/// Validates policy condition documents. A document is a group node
/// (<c>{ match, conditions:[...] }</c>) whose entries are either nested groups
/// or leaf conditions (<c>{ attribute, operator, value }</c>). Pure logic with
/// no persistence dependency so it can be unit tested in isolation.
/// </summary>
public static class PolicyConditionValidator
{
    /// <summary>
    /// Operators supported by leaf conditions. Sourced from the shared
    /// <see cref="PolicyOperators"/> registry so the validator, runtime engine,
    /// and AI prompt cannot drift apart.
    /// </summary>
    public static readonly IReadOnlySet<string> SupportedOperators = PolicyOperators.Ids;

    /// <summary>
    /// Validates the supplied JSON condition document.
    /// </summary>
    /// <returns>An error message when invalid; otherwise <c>null</c>.</returns>
    public static string? Validate(string conditionsJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(conditionsJson);
            if (!document.RootElement.TryGetProperty("conditions", out JsonElement conditions) || conditions.ValueKind != JsonValueKind.Array)
            {
                return "Policy conditions must include a conditions array.";
            }

            return ValidateConditionNodes(document.RootElement);
        }
        catch (JsonException)
        {
            return "Policy conditions must be valid JSON.";
        }
    }

    private static string? ValidateConditionNodes(JsonElement group)
    {
        if (group.TryGetProperty("match", out JsonElement matchElement))
        {
            string? match = matchElement.ValueKind == JsonValueKind.String ? matchElement.GetString() : null;
            if (!PolicyOperators.IsSupportedMatch(match))
            {
                return "Policy condition group match must be 'all', 'any', or 'none'.";
            }
        }

        if (!group.TryGetProperty("conditions", out JsonElement conditions) || conditions.ValueKind != JsonValueKind.Array)
        {
            return "Policy conditions must include a conditions array.";
        }

        foreach (JsonElement node in conditions.EnumerateArray())
        {
            if (node.TryGetProperty("conditions", out JsonElement childConditions) && childConditions.ValueKind == JsonValueKind.Array)
            {
                string? childError = ValidateConditionNodes(node);
                if (childError is not null)
                {
                    return childError;
                }

                continue;
            }

            string? operatorName = node.TryGetProperty("operator", out JsonElement operatorElement) ? operatorElement.GetString() : null;
            if (!PolicyOperators.IsSupported(operatorName))
            {
                return $"Unsupported policy operator '{operatorName}'.";
            }
        }

        return null;
    }
}
