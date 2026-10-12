using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace ShopInventory.Web.Services;

/// <summary>
/// The <see cref="HttpClient"/> each Blazor circuit calls the API through: the named client's
/// pipeline, with <see cref="CircuitApiTokenHandler"/> in front of it.
/// </summary>
/// <remarks>
/// <para>
/// The token handler cannot be added with <c>AddHttpMessageHandler</c>. A handler the client factory
/// builds is resolved from the factory's own scope and shared by every circuit, so it would be
/// handed an auth state provider belonging to nobody; <see cref="ClientAuditHeaders"/> describes the
/// same wall. Built here instead, inside the scoped registration, it is handed the circuit's own
/// service provider and sits outside the shared pipeline, which is reused untouched.
/// </para>
/// <para>
/// Otherwise this is what <see cref="IHttpClientFactory.CreateClient"/> does: the factory's handler,
/// not disposed with the client, and the named client's configuration applied on top. The circuit
/// holds its handler for as long as it lives, exactly as it did before.
/// </para>
/// </remarks>
public static class CircuitApiClient
{
    /// <summary>
    /// Registers the scoped <see cref="HttpClient"/> over the client factory's client
    /// <paramref name="name"/>, which must already be registered.
    /// </summary>
    public static IServiceCollection AddCircuitApiClient(this IServiceCollection services, string name) =>
        services.AddScoped(circuitServices => Create(circuitServices, name));

    private static HttpClient Create(IServiceProvider circuitServices, string name)
    {
        var tokenHandler = new CircuitApiTokenHandler(
            circuitServices,
            circuitServices.GetRequiredService<ILogger<CircuitApiTokenHandler>>())
        {
            InnerHandler = circuitServices.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name)
        };

        var client = new HttpClient(tokenHandler, disposeHandler: false);
        var options = circuitServices.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(name);
        foreach (var configure in options.HttpClientActions)
        {
            configure(client);
        }

        tokenHandler.ApiBaseAddress = client.BaseAddress;
        return client;
    }
}
