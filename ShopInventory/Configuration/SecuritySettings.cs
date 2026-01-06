namespace ShopInventory.Configuration;

/// <summary>
/// Security configuration settings
/// </summary>
public class SecuritySettings
{
    /// <summary>
    /// API keys for service-to-service authentication
    /// </summary>
    public List<ApiKeyConfig> ApiKeys { get; set; } = new();

    /// <summary>
    /// Allowed origins for CORS
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = new();

    /// <summary>
    /// Enable HTTPS redirection
    /// </summary>
    public bool EnforceHttps { get; set; } = true;

    /// <summary>
    /// Enable HSTS (HTTP Strict Transport Security)
    /// </summary>
    public bool EnableHsts { get; set; } = true;

    /// <summary>
    /// Maximum failed login attempts before account lockout
    /// </summary>
    public int MaxFailedLoginAttempts { get; set; } = 5;

    /// <summary>
    /// Account lockout duration in minutes
    /// </summary>
    public int LockoutDurationMinutes { get; set; } = 15;
}

/// <summary>
/// API Key configuration
/// </summary>
public class ApiKeyConfig
{
    /// <summary>
    /// The API key value
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Name/identifier for the API key owner
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Roles assigned to this API key
    /// </summary>
    public List<string> Roles { get; set; } = new();

    /// <summary>
    /// Whether this API key is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Expiration date for the API key (null = never expires)
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
}
