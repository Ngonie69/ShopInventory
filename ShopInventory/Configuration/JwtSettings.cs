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
    public int AccessTokenExpirationMinutes { get; set; } = 60;

    /// <summary>
    /// Refresh token expiration in days
    /// </summary>
    public int RefreshTokenExpirationDays { get; set; } = 7;

    /// <summary>
    /// How long after a refresh token is rotated its predecessor is still accepted, in seconds.
    /// Set to 0 to refuse a rotated token immediately.
    /// </summary>
    /// <remarks>
    /// Rotation without a grace window turns any concurrent refresh into a logout. Production shows
    /// both shapes: two requests 50 ms apart where the second presented the token the first had just
    /// rotated, and a burst of four parallel refreshes after an access token expired with several
    /// requests in flight. The loser of the race is a legitimate client holding what was, moments
    /// ago, a valid token.
    /// <para>
    /// The window is deliberately short. Outside it, a rotated token being presented again is the
    /// signal that matters — it means someone is replaying a token that is no longer current — and
    /// it is still refused.
    /// </para>
    /// </remarks>
    public int RefreshTokenRotationGraceSeconds { get; set; } = 60;
}
