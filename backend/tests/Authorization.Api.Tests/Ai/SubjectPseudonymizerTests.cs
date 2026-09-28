using Authorization.Api.Ai;

namespace Authorization.Api.Tests.Ai;

/// <summary>
/// Unit tests for <see cref="SubjectPseudonymizer"/>, the PII boundary for AI features that reason
/// about a subject (F4/F9). Tokens must be stable within a request, must not contain the email, and
/// blank input must map to a fixed placeholder.
/// </summary>
public sealed class SubjectPseudonymizerTests
{
    [Fact]
    public void Pseudonymize_IsStableForSameEmailWithinInstance()
    {
        var pseudonymizer = new SubjectPseudonymizer();

        string first = pseudonymizer.Pseudonymize("user13.reviewer@icis.com");
        string second = pseudonymizer.Pseudonymize("USER13.REVIEWER@icis.com");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Pseudonymize_DoesNotLeakTheEmail()
    {
        var pseudonymizer = new SubjectPseudonymizer();

        string token = pseudonymizer.Pseudonymize("user13.reviewer@icis.com");

        Assert.StartsWith("user-", token);
        Assert.DoesNotContain("reviewer", token, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", token);
        Assert.DoesNotContain("icis", token, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pseudonymize_DistinctEmailsProduceDistinctTokens()
    {
        var pseudonymizer = new SubjectPseudonymizer();

        Assert.NotEqual(
            pseudonymizer.Pseudonymize("alice@icis.com"),
            pseudonymizer.Pseudonymize("bob@icis.com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Pseudonymize_BlankEmail_MapsToPlaceholder(string? email)
    {
        var pseudonymizer = new SubjectPseudonymizer();

        Assert.Equal("unknown-subject", pseudonymizer.Pseudonymize(email));
    }

    [Fact]
    public void Pseudonymize_UsesPerInstanceSalt_SoTokensDifferAcrossReviews()
    {
        string a = new SubjectPseudonymizer().Pseudonymize("user13.reviewer@icis.com");
        string b = new SubjectPseudonymizer().Pseudonymize("user13.reviewer@icis.com");

        Assert.NotEqual(a, b);
    }
}
