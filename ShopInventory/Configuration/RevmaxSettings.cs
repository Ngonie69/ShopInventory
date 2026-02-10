namespace ShopInventory.Configuration;

/// <summary>
/// Configuration settings for REVMax fiscal integration.
/// </summary>
public class RevmaxSettings
{
    public const string SectionName = "Revmax";

    /// <summary>
    /// Base URL for REVMax API (e.g., http://172.16.16.201:8001)
    /// </summary>
    public string BaseUrl { get; set; } = "http://172.16.16.201:8001";

    /// <summary>
    /// HTTP request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Default currency code (e.g., ZWG).
    /// </summary>
    public string DefaultCurrency { get; set; } = "ZWG";

    /// <summary>
    /// Default branch name for transactions.
    /// </summary>
    public string DefaultBranchName { get; set; } = "Kefalos";

    /// <summary>
    /// Default reference device ID.
    /// </summary>
    public int DefaultRefDeviceId { get; set; } = 22862;

    /// <summary>
    /// VAT rate as decimal (15.5% = 0.155). Effective 1 January 2026.
    /// </summary>
    public decimal VatRate { get; set; } = 0.155m;

    /// <summary>
    /// Whether to forward API key to upstream.
    /// </summary>
    public bool ForwardApiKey { get; set; } = false;

    /// <summary>
    /// API key mode (Query or Header).
    /// </summary>
    public string ApiKeyMode { get; set; } = "Query";

    /// <summary>
    /// API key parameter name.
    /// </summary>
    public string ApiKeyName { get; set; } = "api_key";

    /// <summary>
    /// Maximum retry attempts for transient failures.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Retry backoff delays in milliseconds.
    /// </summary>
    public int[] RetryDelaysMs { get; set; } = [200, 500, 1000];
}
