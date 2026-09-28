using System.Security.Cryptography;
using System.Text;
using Authorization.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Authorization.Api.Authentication;

public sealed class RuntimeClientCredentialValidator
{
    public const string CallerUnauthenticated = "CALLER_UNAUTHENTICATED";
    public const string CallerApplicationMismatch = "CALLER_APPLICATION_MISMATCH";

    private readonly RuntimeClientOptions options;

    public RuntimeClientCredentialValidator(IOptions<RuntimeClientOptions> options)
    {
        this.options = options.Value;
    }

    public RuntimeClientValidationResult Validate(string? clientId, string? clientSecret, string requestedApplicationId)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return RuntimeClientValidationResult.Failure(CallerUnauthenticated);
        }

        RuntimeClientCredentialOptions? credential = FindCredential(clientId);
        if (credential is null || !SecretEquals(credential.ClientSecret, clientSecret))
        {
            return RuntimeClientValidationResult.Failure(CallerUnauthenticated);
        }

        return credential.ApplicationId.Equals(requestedApplicationId, StringComparison.OrdinalIgnoreCase)
            ? RuntimeClientValidationResult.Success(credential.ApplicationId, "SERVICE_ACCOUNT", credential.ClientId)
            : RuntimeClientValidationResult.Failure(CallerApplicationMismatch);
    }

    private RuntimeClientCredentialOptions? FindCredential(string clientId)
    {
        return options.Clients.FirstOrDefault(credential =>
            credential.ClientId.Equals(clientId, StringComparison.Ordinal));
    }

    private static bool SecretEquals(string expected, string actual)
    {
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] actualBytes = Encoding.UTF8.GetBytes(actual);

        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}