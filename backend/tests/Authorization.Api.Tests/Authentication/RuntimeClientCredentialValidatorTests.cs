using Authorization.Api.Authentication;
using Authorization.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Authorization.Api.Tests.Authentication;

public sealed class RuntimeClientCredentialValidatorTests
{
    [Fact]
    public void Validate_AcceptsMatchingClientAndApplication()
    {
        var validator = new RuntimeClientCredentialValidator(Options.Create(CreateOptions()));

        RuntimeClientValidationResult result = validator.Validate("finance-runtime-client", "finance_dev_secret", "finance-app");

        Assert.True(result.Succeeded);
        Assert.Equal("finance-app", result.ApplicationId);
    }

    [Fact]
    public void Validate_RejectsMissingCredentials()
    {
        var validator = new RuntimeClientCredentialValidator(Options.Create(CreateOptions()));

        RuntimeClientValidationResult result = validator.Validate(null, null, "finance-app");

        Assert.False(result.Succeeded);
        Assert.Equal(RuntimeClientCredentialValidator.CallerUnauthenticated, result.ErrorCode);
    }

    [Fact]
    public void Validate_RejectsInvalidSecret()
    {
        var validator = new RuntimeClientCredentialValidator(Options.Create(CreateOptions()));

        RuntimeClientValidationResult result = validator.Validate("finance-runtime-client", "wrong", "finance-app");

        Assert.False(result.Succeeded);
        Assert.Equal(RuntimeClientCredentialValidator.CallerUnauthenticated, result.ErrorCode);
    }

    [Fact]
    public void Validate_RejectsCallerApplicationMismatch()
    {
        var validator = new RuntimeClientCredentialValidator(Options.Create(CreateOptions()));

        RuntimeClientValidationResult result = validator.Validate("finance-runtime-client", "finance_dev_secret", "crm-app");

        Assert.False(result.Succeeded);
        Assert.Equal(RuntimeClientCredentialValidator.CallerApplicationMismatch, result.ErrorCode);
    }

    private static RuntimeClientOptions CreateOptions()
    {
        return new RuntimeClientOptions
        {
            Clients =
            [
                new RuntimeClientCredentialOptions
                {
                    ApplicationId = "finance-app",
                    ClientId = "finance-runtime-client",
                    ClientSecret = "finance_dev_secret",
                },
                new RuntimeClientCredentialOptions
                {
                    ApplicationId = "crm-app",
                    ClientId = "crm-runtime-client",
                    ClientSecret = "crm_dev_secret",
                },
                new RuntimeClientCredentialOptions
                {
                    ApplicationId = "hr-app",
                    ClientId = "hr-runtime-client",
                    ClientSecret = "hr_dev_secret",
                },
            ],
        };
    }
}