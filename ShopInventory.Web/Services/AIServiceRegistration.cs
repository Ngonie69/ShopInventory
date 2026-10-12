namespace ShopInventory.Web.Services;

/// <summary>
/// Registers the AI assistant with an HttpClient of its own for the provider.
/// </summary>
/// <remarks>
/// The provider is somebody else's host. The assistant must never be handed the circuit's API
/// client: that one carries the portal's <c>X-API-Key</c> and the signed-in user's bearer token as
/// default headers, and HttpClient puts default headers on every request whatever host it is for.
/// The client here is given no default headers, no base address and no handlers, so the only
/// credential on a request is the provider's own key, set by <see cref="AIService"/> per request.
/// <c>AIServiceProviderCredentialTests</c> holds it to that.
/// </remarks>
public static class AIServiceRegistration
{
    public static IServiceCollection AddAIService(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AISettings>(configuration.GetSection("AI"));

        // Somebody is waiting on the chat page for the answer, so this is well short of the five
        // minutes the API's client allows for slow SAP reads.
        services.AddHttpClient("AiProvider", client => client.Timeout = TimeSpan.FromMinutes(2));

        services.AddScoped<IAIService, AIService>();

        return services;
    }
}
