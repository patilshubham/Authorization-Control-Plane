namespace Authorization.Api.Configuration;

public sealed class RuntimeClientOptions
{
    public const string SectionName = "RuntimeClients";

    /// <summary>
    /// Registered runtime callers keyed by their client credentials. Populated from configuration
    /// with dev/test bootstrap values; production deployments supply real credentials via secrets.
    /// </summary>
    public IReadOnlyList<RuntimeClientCredentialOptions> Clients { get; init; } = [];
}

public sealed class RuntimeClientCredentialOptions
{
    public string ApplicationId { get; init; } = string.Empty;

    public string ClientId { get; init; } = string.Empty;

    public string ClientSecret { get; init; } = string.Empty;
}