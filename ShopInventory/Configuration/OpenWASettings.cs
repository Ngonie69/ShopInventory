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
    public string[] WebhookEvents { get; set; } =
    [
        "message.received",
        "message.sent",
        "message.ack",
        "message.revoked",
        "session.status",
        "session.qr",
        "session.authenticated",
        "session.disconnected"
    ];

    /// <summary>
    /// Ordered health endpoint candidates used to tolerate doc/runtime differences.
    /// </summary>
    public string[] HealthEndpointPaths { get; set; } = ["/health/detailed", "/api/health", "/health"];
}
