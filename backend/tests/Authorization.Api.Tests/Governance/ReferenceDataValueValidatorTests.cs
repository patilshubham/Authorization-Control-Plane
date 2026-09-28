using Authorization.Api.Governance;

namespace Authorization.Api.Tests.Governance;

public sealed class ReferenceDataValueValidatorTests
{
    [Fact]
    public void Validate_ReturnsNull_ForJsonArray()
    {
        Assert.Null(ReferenceDataValueValidator.Validate("[\"US\",\"CA\"]"));
    }

    [Fact]
    public void Validate_ReturnsNull_ForJsonObject()
    {
        Assert.Null(ReferenceDataValueValidator.Validate("{\"maxAmount\":\"1000\"}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ReturnsError_ForEmpty(string? json)
    {
        Assert.Equal("Reference data value must not be empty.", ReferenceDataValueValidator.Validate(json));
    }

    [Fact]
    public void Validate_ReturnsError_ForMalformedJson()
    {
        Assert.Equal("Reference data value must be valid JSON.", ReferenceDataValueValidator.Validate("{not json"));
    }

    [Theory]
    [InlineData("\"just-a-string\"")]
    [InlineData("42")]
    [InlineData("true")]
    public void Validate_ReturnsError_ForScalarRoot(string json)
    {
        Assert.Equal("Reference data value must be a JSON array or object.", ReferenceDataValueValidator.Validate(json));
    }
}
