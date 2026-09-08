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
    /// Event types the gateway webhook subscribes to. These are OpenWA's own event names.
    /// </summary>
    /// <remarks>
    /// Empty rather than defaulted, and both collections below are the same: the configuration binder
    /// appends to a collection that already holds items, so a default here plus the same list in
    /// appsettings.json bound to both copies. Neither ever showed a symptom — the registrar and the
    /// client both deduplicate before use — but a value that silently doubles cannot be trusted to be
    /// removable, because the code's copy puts it back. The lists live in appsettings.json alone.
    /// </remarks>
    public string[] WebhookEvents { get; set; } = [];

    /// <summary>
    /// Ordered health endpoint candidates used to tolerate doc/runtime differences.
    /// </summary>
    public string[] HealthEndpointPaths { get; set; } = [];
}
