namespace ShopInventory.Configuration;

/// <summary>
/// Rate limiting configuration settings for DDoS protection
/// </summary>
public class RateLimitSettings
{
    /// <summary>
    /// Maximum number of requests allowed within the time window
    /// </summary>
    public int PermitLimit { get; set; } = 100;

    /// <summary>
    /// Time window in seconds for rate limiting
    /// </summary>
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum number of requests that can be queued when limit is reached
    /// </summary>
    public int QueueLimit { get; set; } = 10;

    /// <summary>
    /// Stricter limit for authentication endpoints (login, register)
    /// </summary>
    public int AuthEndpointPermitLimit { get; set; } = 10;

    /// <summary>
    /// Time window in seconds for auth endpoint rate limiting
    /// </summary>
    public int AuthEndpointWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Enable/disable IP-based rate limiting
    /// </summary>
    public bool EnableIpRateLimiting { get; set; } = true;

    /// <summary>
    /// Whitelisted IP addresses exempt from rate limiting
    /// </summary>
    public List<string> IpWhitelist { get; set; } = new();
}
