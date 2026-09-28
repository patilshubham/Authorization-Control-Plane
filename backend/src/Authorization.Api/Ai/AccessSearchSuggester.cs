using Authorization.Ai;

namespace Authorization.Api.Ai;

/// <summary>
/// Guidance produced when a natural-language question cannot be confidently answered.
/// <see cref="Reason"/> explains, in plain language, why the original phrasing did not work;
/// <see cref="Intents"/> are the most likely interpretations (rendered as "Are you trying to …?");
/// <see cref="Suggestions"/> are ready-to-run prompts that are guaranteed to be inside the engine's
/// capabilities and grounded in the application's real vocabulary.
/// </summary>
public sealed record AccessSearchGuidance(
    string Reason,
    IReadOnlyList<string> Intents,
    IReadOnlyList<string> Suggestions);

/// <summary>
/// Turns a failed / low-confidence access-search question into helpful, answerable guidance.
///
/// This is intentionally <b>deterministic and grounded</b>: every suggestion it emits is phrased in
/// the exact shape the planner understands and only references entities, fields, relationships and
/// values that exist in the closed schema (<see cref="AccessSearchExecutor"/>) or the caller's real
/// <see cref="AccessSearchVocabulary"/>. It never invents data. When the AI planner (which is given
/// the full schema and vocabulary) offers its own grounded follow-ups, those are preferred; the
/// deterministic buckets below act as an always-available fallback.
/// </summary>
public static class AccessSearchSuggester
{
    private const int MaxSuggestions = 5;
    private const int MaxIntents = 3;

    public static AccessSearchGuidance Build(
        string question,
        AccessSearchVocabulary vocabulary,
        string reason,
        IReadOnlyList<string>? aiSuggestions = null)
    {
        string q = (question ?? string.Empty).ToLowerInvariant();

        // Grounded sample values: only used when they actually exist so a suggestion is always answerable.
        string? role = First(vocabulary.RoleKeys);
        string? permission = First(vocabulary.PermissionKeys);
        string? action = First(vocabulary.Actions);
        string? resource = First(vocabulary.Resources);

        var intents = new List<string>();
        var suggestions = new List<string>();

        void AddIntent(string text)
        {
            if (!intents.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                intents.Add(text);
            }
        }

        void AddSuggestion(string text)
        {
            if (!suggestions.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                suggestions.Add(text);
            }
        }

        // The model was given the full schema (entities, fields, enum domains) and vocabulary, so when
        // it offers grounded follow-ups we prefer them — they are specific to the user's phrasing.
        if (aiSuggestions is { Count: > 0 })
        {
            foreach (string s in aiSuggestions)
            {
                if (!string.IsNullOrWhiteSpace(s))
                {
                    AddSuggestion(s.Trim());
                }
            }
        }

        bool Mentions(params string[] words) => words.Any(w => q.Contains(w));

        // "past expiry", "expired", "lapsed" → assignments in state EXPIRED is fully answerable.
        if (Mentions("expir", "past due", "lapsed", "overdue", "no longer valid"))
        {
            AddIntent("find access assignments that have expired");
            AddSuggestion("How many assignments are in state EXPIRED?");
            AddSuggestion("Show assignments in state EXPIRED.");
        }

        // "inactive"/"disabled"/"status": roles and permissions now carry a status field, so steer to the
        // real, answerable status/state filters instead of a vague alternative.
        if (Mentions("inactive", "disabled", "deactivated", "suspended", "revoked", "status", "not active", "archived", "deprecated"))
        {
            AddIntent("filter records by their lifecycle status");
            if (Mentions("assignment", "grant", "user", "subject", "people"))
            {
                AddSuggestion("How many assignments are in state REVOKED?");
                AddSuggestion("Show assignments in state EXPIRED.");
            }

            if (Mentions("permission"))
            {
                AddSuggestion("How many permissions are not in status ACTIVE?");
            }

            if (Mentions("role") || (!Mentions("assignment", "permission", "grant", "user", "subject", "people")))
            {
                AddSuggestion("How many roles are not in status ACTIVE?");
                AddSuggestion("Show roles in status DISABLED.");
            }
        }

        // People / subjects.
        if (Mentions("who can", "who has", "which user", "which people", "users", "people", "subject", "employee"))
        {
            AddIntent("find the people (subjects) who hold a role or permission");
            if (action is not null && resource is not null)
            {
                AddSuggestion($"Which subjects can {action} {resource}?");
            }

            if (role is not null)
            {
                AddSuggestion($"Show all subjects with the {role} role.");
            }

            if (permission is not null)
            {
                AddSuggestion($"Which subjects have the {permission} permission?");
            }
        }

        // Roles.
        if (Mentions("role"))
        {
            AddIntent("explore roles and what they grant");
            AddSuggestion("Which roles are privileged?");
            if (permission is not null)
            {
                AddSuggestion($"Which roles grant {permission}?");
            }

            AddSuggestion("How many permissions does each role grant?");
        }

        // Permissions.
        if (Mentions("permission", "entitlement", "grant"))
        {
            AddIntent("see how a permission is granted");
            if (permission is not null)
            {
                AddSuggestion($"Which roles grant {permission}?");
            }

            AddSuggestion("How many roles grant each permission?");
        }

        // Policies / deny.
        if (Mentions("policy", "policies", "deny", "denied", "block", "forbid", "rule"))
        {
            AddIntent("review the authorization policies");
            AddSuggestion("Which policies deny access?");
            if (permission is not null)
            {
                AddSuggestion($"Show policies for {permission}.");
            }
        }

        // Counting.
        if (Mentions("how many", "count", "number of", "total", "how much"))
        {
            AddIntent("get a count rather than a list");
            AddSuggestion("How many roles are privileged?");
            AddSuggestion("How many assignments are in state EXPIRED?");
        }

        // Fallbacks: always leave the user with at least two concrete, high-value starting points.
        if (suggestions.Count < 2)
        {
            AddSuggestion("Which roles are privileged?");
            AddSuggestion("Which policies deny access?");
            if (role is not null)
            {
                AddSuggestion($"Show the {role} role and its permissions.");
            }
        }

        return new AccessSearchGuidance(
            reason,
            intents.Take(MaxIntents).ToList(),
            suggestions.Take(MaxSuggestions).ToList());
    }

    private static string? First(IReadOnlyList<string> values) =>
        values is { Count: > 0 } ? values[0] : null;
}
