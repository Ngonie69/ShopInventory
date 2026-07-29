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
    /// Ordered health endpoint candidates used to tolerate doc/runtime differences.
    /// </summary>
    public string[] HealthEndpointPaths { get; set; } = ["/health/detailed", "/api/health", "/health"];
}