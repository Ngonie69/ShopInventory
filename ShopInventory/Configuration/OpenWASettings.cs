namespace ShopInventory.Configuration;

/// <summary>
/// Configuration settings for the OpenWA WhatsApp gateway.
/// </summary>
public class OpenWASettings
{
    public const string SectionName = "OpenWA";

    /// <summary>
    /// Whether the OpenWA integration is enabled.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Base URL for the OpenWA host.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:2785";

    /// <summary>
    /// API key used when calling OpenWA.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// HTTP request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Shared secret used to authenticate inbound OpenWA webhooks.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// The absolute URL OpenWA posts inbound events to. OpenWA validates this with an @IsUrl
    /// check that rejects "localhost", so a same-host deployment must name 127.0.0.1 or the
    /// machine's LAN address rather than localhost.
    /// </summary>
    public string WebhookPublicUrl { get; set; } = string.Empty;

    /// <summary>
    /// Event types the gateway webhook subscribes to. These are OpenWA's own event names, supplied
    /// entirely by <c>OpenWA:WebhookEvents</c> in appsettings.json — deliberately left empty here.
    /// </summary>
    /// <remarks>
    /// Do not give this property a collection initializer. The configuration binder APPENDS to a
    /// collection that already holds items rather than replacing it, so a default here plus the same
    /// list in appsettings.json binds to every event twice. <c>NormalizedEvents</c> in
    /// <c>OpenWAWebhookRegistrar</c> deduplicates before subscribing, so that was never visible —
    /// but <c>DailyStockSettings.MonitoredWarehouses</c> had the identical shape without a
    /// deduplicating reader and doubled a job's SAP reads. <c>OptionsCollectionBindingTests</c> pins
    /// the binder behaviour and fails if the shape returns.
    ///
    /// The registrar falls back to <c>message.received</c> when this ends up empty, because OpenWA's
    /// CreateWebhookDto enforces ArrayMinSize(1) once events are supplied at all.
    /// </remarks>
    public string[] WebhookEvents { get; set; } = [];

    /// <summary>
    /// Ordered health endpoint candidates used to tolerate doc/runtime differences. Supplied
    /// entirely by <c>OpenWA:HealthEndpointPaths</c> in appsettings.json — empty here for the same
    /// binder reason as <see cref="WebhookEvents"/>. An empty list is reported as a configuration
    /// error rather than probed blindly.
    /// </summary>
    public string[] HealthEndpointPaths { get; set; } = [];
}
