using Authorization.Sdk;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration helpers for the runtime <see cref="IAuthorizationClient"/>. These wire the client as a
/// typed <see cref="System.Net.Http.HttpClient"/> so callers get connection pooling, DI-managed
/// lifetime, and the ability to chain additional handlers (logging, resilience, telemetry).
/// </summary>
public static class AuthorizationClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IAuthorizationClient"/> using a fixed <see cref="AuthorizationClientOptions"/>.
    /// </summary>
    /// <param name="baseAddress">
    /// Base address of the authorization API. Include a trailing slash (e.g. <c>https://host/</c>) so the
    /// relative <c>v1/authorize</c> paths resolve correctly.
    /// </param>
    public static IHttpClientBuilder AddAuthorizationClient(
        this IServiceCollection services,
        Uri baseAddress,
        AuthorizationClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return services.AddAuthorizationClient(baseAddress, _ => options);
    }

    /// <summary>
    /// Registers <see cref="IAuthorizationClient"/>, resolving <see cref="AuthorizationClientOptions"/>
    /// from the service provider so the access-token provider can depend on other registered services
    /// (for example a cached client-credentials token source).
    /// </summary>
    /// <param name="baseAddress">
    /// Base address of the authorization API. Include a trailing slash (e.g. <c>https://host/</c>) so the
    /// relative <c>v1/authorize</c> paths resolve correctly.
    /// </param>
    public static IHttpClientBuilder AddAuthorizationClient(
        this IServiceCollection services,
        Uri baseAddress,
        Func<IServiceProvider, AuthorizationClientOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(optionsFactory);

        services.TryAddSingleton(optionsFactory);

        return services.AddHttpClient<IAuthorizationClient, AuthorizationClient>(client =>
        {
            client.BaseAddress = baseAddress;
        });
    }
}
