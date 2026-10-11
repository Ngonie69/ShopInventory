using ShopInventory.Configuration;

namespace ShopInventory.Services.SlHealth;

public static class SlHealthServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SL Health reader: its settings, the typed client, the in-memory copy of the last
    /// answer and the refresher that keeps it current. With <c>SlHealth:Url</c> or
    /// <c>SlHealth:Token</c> empty everything is still registered and nothing is ever sent.
    /// </summary>
    public static IHttpClientBuilder AddSlHealth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SlHealthSettings>(configuration.GetSection(SlHealthSettings.SectionName));
        services.AddSingleton<SlHealthMonitor>();
        services.AddHostedService<SlHealthRefresher>();

        return services
            .AddHttpClient<ISlHealthClient, SlHealthClient>(client =>
            {
                client.Timeout = SlHealthClient.Timeout;
                client.MaxResponseContentBufferSize = SlHealthClient.MaxResponseBytes;
            })
            // The factory's own loggers write each request, and any transport exception whole. Nothing
            // about a request that carries the token is logged except what SlHealthMonitor chooses to say.
            .RemoveAllLoggers();
    }
}
