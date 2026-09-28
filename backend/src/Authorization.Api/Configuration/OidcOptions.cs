using System.ComponentModel.DataAnnotations;

namespace Authorization.Api.Configuration;

public sealed class OidcOptions
{
    public const string SectionName = "Oidc";

    [Required]
    public string Authority { get; init; } = string.Empty;

    [Required]
    public string Audience { get; init; } = string.Empty;

    [Required]
    public string PortalClientId { get; init; } = string.Empty;

    /// <summary>
    /// Optional OpenID Connect discovery document address used for back-channel metadata
    /// retrieval (e.g. an internal Docker network URL) when it differs from the public
    /// <see cref="Authority"/> used to validate the token issuer. When empty, the
    /// <see cref="Authority"/> is used for both metadata retrieval and issuer validation.
    /// </summary>
    public string MetadataAddress { get; init; } = string.Empty;
}