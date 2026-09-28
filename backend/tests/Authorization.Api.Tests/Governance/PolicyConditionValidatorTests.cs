using Authorization.Api.Governance;

namespace Authorization.Api.Tests.Governance;

public sealed class PolicyConditionValidatorTests
{
    [Fact]
    public void Validate_ReturnsNull_ForValidLeafConditions()
    {
        const string json = """{"match":"all","conditions":[{"attribute":"dept","operator":"eq","value":"eng"}]}""";

        Assert.Null(PolicyConditionValidator.Validate(json));
    }

    [Fact]
    public void Validate_ReturnsNull_ForNestedGroups()
    {
        const string json = """
        {"match":"any","conditions":[
            {"attribute":"region","operator":"in","value":["us","eu"]},
            {"match":"all","conditions":[{"attribute":"tier","operator":"gte","value":3}]}
        ]}
        """;

        Assert.Null(PolicyConditionValidator.Validate(json));
    }

    [Fact]
    public void Validate_ReturnsError_ForMalformedJson()
    {
        Assert.Equal("Policy conditions must be valid JSON.", PolicyConditionValidator.Validate("{not json"));
    }

    [Fact]
    public void Validate_ReturnsError_WhenConditionsArrayMissing()
    {
        Assert.Equal("Policy conditions must include a conditions array.", PolicyConditionValidator.Validate("""{"match":"all"}"""));
    }

    [Fact]
    public void Validate_ReturnsError_ForInvalidMatchKeyword()
    {
        string? result = PolicyConditionValidator.Validate("""{"match":"some","conditions":[]}""");

        Assert.Equal("Policy condition group match must be 'all', 'any', or 'none'.", result);
    }

    [Fact]
    public void Validate_ReturnsNull_ForNoneMatchKeyword()
    {
        Assert.Null(PolicyConditionValidator.Validate("""{"match":"none","conditions":[{"attribute":"x","operator":"eq","value":"y"}]}"""));
    }

    [Theory]
    [InlineData("notContains")]
    [InlineData("matches")]
    [InlineData("notMatches")]
    [InlineData("containsAny")]
    [InlineData("containsAll")]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("between")]
    [InlineData("isTrue")]
    [InlineData("isFalse")]
    public void Validate_ReturnsNull_ForEnhancedOperators(string operatorName)
    {
        Assert.Null(PolicyConditionValidator.Validate($$"""{"conditions":[{"attribute":"x","operator":"{{operatorName}}","value":"y"}]}"""));
    }

    [Fact]
    public void Validate_ReturnsError_ForUnsupportedOperator()
    {
        string? result = PolicyConditionValidator.Validate("""{"conditions":[{"attribute":"x","operator":"regexp","value":"y"}]}""");

        Assert.Equal("Unsupported policy operator 'regexp'.", result);
    }
}
