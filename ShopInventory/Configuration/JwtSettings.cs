namespace ShopInventory.Configuration;

/// <summary>
/// JWT authentication configuration settings
/// </summary>
public class JwtSettings
{
    /// <summary>
    /// Secret key used for signing JWT tokens (minimum 32 characters for HS256)
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Token issuer (typically your API's URL)
    /// </summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// Token audience (typically your API's URL or client application)
    /// </summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Access token expiration in minutes
    /// </summary>
    public int AccessTokenExpirationMinutes { get; set; } = 15;

    /// <summary>
    /// Refresh token expiration in days
    /// </summary>
    public int RefreshTokenExpirationDays { get; set; } = 7;
}
