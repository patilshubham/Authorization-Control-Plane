using Authorization.Api.Governance;

namespace Authorization.Api.Tests.Governance;

public sealed class PolicyObligationsValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    public void Validate_ReturnsNull_ForEmptyOrAbsent(string? json)
    {
        Assert.Null(PolicyObligationsValidator.Validate(json));
    }

    [Fact]
    public void Validate_ReturnsNull_ForArrayOfStringIds()
    {
        Assert.Null(PolicyObligationsValidator.Validate("[\"require_mfa\",\"log_access\"]"));
    }

    [Fact]
    public void Validate_ReturnsNull_ForObjectsWithIdAndOptionalValue()
    {
        Assert.Null(PolicyObligationsValidator.Validate("[{\"id\":\"mask_ssn\",\"value\":\"last4\"},{\"id\":\"require_mfa\"}]"));
    }

    [Fact]
    public void Validate_ReturnsError_ForMalformedJson()
    {
        Assert.Equal("Obligations must be valid JSON.", PolicyObligationsValidator.Validate("{not json"));
    }

    [Fact]
    public void Validate_ReturnsError_WhenRootIsNotArray()
    {
        Assert.Equal("Obligations must be a JSON array.", PolicyObligationsValidator.Validate("{\"id\":\"x\"}"));
    }

    [Fact]
    public void Validate_ReturnsError_WhenObjectMissingStringId()
    {
        Assert.Equal("Each obligation object must include a string 'id'.", PolicyObligationsValidator.Validate("[{\"value\":\"x\"}]"));
    }

    [Fact]
    public void Validate_ReturnsError_WhenValueIsNotString()
    {
        Assert.Equal("Obligation 'value' must be a string when provided.", PolicyObligationsValidator.Validate("[{\"id\":\"x\",\"value\":5}]"));
    }

    [Fact]
    public void Validate_ReturnsError_WhenEntryIsNeitherStringNorObject()
    {
        Assert.Equal("Each obligation must be a string id or an object with an 'id'.", PolicyObligationsValidator.Validate("[5]"));
    }

    [Fact]
    public void Validate_ReturnsError_ForEmptyId()
    {
        Assert.Equal("Obligation id must not be empty.", PolicyObligationsValidator.Validate("[\"\"]"));
    }

    [Fact]
    public void Validate_ReturnsError_ForDuplicateIds()
    {
        Assert.Equal("Duplicate obligation id 'require_mfa'.", PolicyObligationsValidator.Validate("[\"require_mfa\",\"require_mfa\"]"));
    }
}
