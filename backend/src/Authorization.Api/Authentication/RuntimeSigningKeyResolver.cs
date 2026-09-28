using System.Collections.Concurrent;
using Microsoft.IdentityModel.Tokens;

namespace Authorization.Api.Authentication;

/// <summary>
/// Resolves the JWKS signing keys used to validate runtime-caller tokens for a given OIDC provider.
/// </summary>
public interface IRuntimeSigningKeyResolver
{
    Task<IReadOnlyCollection<SecurityKey>> ResolveAsync(string jwksUri, CancellationToken cancellationToken);
}

/// <summary>
/// Fetches and caches JWKS documents from each OIDC provider's <c>jwks_uri</c>. Keys are cached per
/// URI so signature validation does not hit the identity provider on every authorization request.
/// </summary>
public sealed class JwksRuntimeSigningKeyResolver : IRuntimeSigningKeyResolver
{
    public const string HttpClientName = "runtime-jwks";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory httpClientFactory;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);

    public JwksRuntimeSigningKeyResolver(IHttpClientFactory httpClientFactory, TimeProvider? timeProvider = null)
    {
        this.httpClientFactory = httpClientFactory;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyCollection<SecurityKey>> ResolveAsync(string jwksUri, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (cache.TryGetValue(jwksUri, out CacheEntry? cached) && cached.ExpiresAt > now)
        {
            return cached.Keys;
        }

        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        string json = await client.GetStringAsync(jwksUri, cancellationToken);
        IReadOnlyCollection<SecurityKey> keys = new JsonWebKeySet(json).GetSigningKeys().ToArray();
        cache[jwksUri] = new CacheEntry(keys, now.Add(CacheDuration));
        return keys;
    }

    private sealed record CacheEntry(IReadOnlyCollection<SecurityKey> Keys, DateTimeOffset ExpiresAt);
}
