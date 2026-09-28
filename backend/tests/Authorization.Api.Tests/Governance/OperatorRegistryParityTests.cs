using System.Text.RegularExpressions;
using Authorization.Ai;
using Authorization.Infrastructure.RuntimeAuthorization;

namespace Authorization.Api.Tests.Governance;

/// <summary>
/// Guards the "single source of truth" invariant: the backend <see cref="PolicyOperators"/>
/// registry, the AI drafting mirror (<see cref="AiPolicyOperators"/>), and the frontend
/// condition model must all support the exact same operator set. Adding an operator in one
/// place without the others fails these tests.
/// </summary>
public sealed class OperatorRegistryParityTests
{
    [Fact]
    public void AiOperatorMirror_MatchesRegistry()
    {
        Assert.Equal(
            PolicyOperators.Ids.OrderBy(id => id, StringComparer.Ordinal),
            AiPolicyOperators.Ids.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void FrontendModel_MatchesRegistry()
    {
        string? modelPath = FindFrontendModelPath();
        // The frontend source is only present in a full checkout; skip gracefully otherwise.
        if (modelPath is null)
        {
            return;
        }

        string source = File.ReadAllText(modelPath);
        // Capture the operator ids from the OperatorId union type.
        Match union = Regex.Match(source, @"export type OperatorId =([\s\S]*?);");
        Assert.True(union.Success, "Could not locate the OperatorId union in model.ts.");

        HashSet<string> frontendIds = Regex
            .Matches(union.Groups[1].Value, "\"([a-zA-Z]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            PolicyOperators.Ids.OrderBy(id => id, StringComparer.Ordinal),
            frontendIds.OrderBy(id => id, StringComparer.Ordinal));
    }

    private static string? FindFrontendModelPath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "frontend", "src", "workspace", "conditions", "model.ts");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
