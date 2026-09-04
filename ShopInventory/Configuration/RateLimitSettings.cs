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

    /// <summary>
    /// API keys exempt from rate limiting, matched against the <c>X-API-Key</c> header.
    /// </summary>
    /// <remarks>
    /// Exempts a key from throttling and nothing else — it grants no access. A key still has to be
    /// a real one under <c>Security:ApiKeys</c> to authenticate at all, and this list is not a way
    /// to create one.
    /// </remarks>
    public List<string> ApiKeyWhitelist { get; set; } = new();

    /// <summary>
    /// How long <see cref="Services.IRateLimitService"/> blocks a client for once it blocks one.
    /// </summary>
    /// <remarks>
    /// Belongs to the <c>/api/RateLimit</c> bookkeeping rather than to the ASP.NET Core limiter,
    /// which does not block clients at all. It lives here so the whole of
    /// <c>RateLimitConfigDto</c> has one home.
    /// </remarks>
    public int BlockDurationMinutes { get; set; } = 15;

    /// <summary>
    /// A short stable hash of everything that decides how a client is limited.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Goes in front of every rate limit partition key, because ASP.NET Core builds a partition's
    /// options <b>once</b>, when that partition is first created, and then caches the limiter under
    /// its key. Without this, changing a limit would not touch any client already being limited -
    /// which is exactly the client somebody is changing it for. Including the settings in the key
    /// means changed settings are a different partition, built fresh with the new options, while
    /// unchanged settings keep the same key and the same partition.
    /// </para>
    /// <para>
    /// The cost is deliberate: a change gives every client a new window, so the moment after one
    /// each client has its full allowance again regardless of what it had just spent. Limits move
    /// rarely and a change is a decision someone is making on purpose; a change that silently did
    /// not apply would be far worse than one that is briefly generous.
    /// </para>
    /// </remarks>
    public string Fingerprint()
    {
        var canonical = string.Join(
            '|',
            PermitLimit, WindowSeconds, QueueLimit,
            AuthEndpointPermitLimit, AuthEndpointWindowSeconds,
            EnableIpRateLimiting,
            string.Join(',', IpWhitelist.OrderBy(entry => entry, StringComparer.Ordinal)),
            string.Join(',', ApiKeyWhitelist.OrderBy(entry => entry, StringComparer.Ordinal)));

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash, 0, 4);
    }

    /// <summary>
    /// A copy, so a caller cannot mutate a snapshot other requests are reading.
    /// </summary>
    public RateLimitSettings Clone() => new()
    {
        PermitLimit = PermitLimit,
        WindowSeconds = WindowSeconds,
        QueueLimit = QueueLimit,
        AuthEndpointPermitLimit = AuthEndpointPermitLimit,
        AuthEndpointWindowSeconds = AuthEndpointWindowSeconds,
        EnableIpRateLimiting = EnableIpRateLimiting,
        IpWhitelist = [.. IpWhitelist],
        ApiKeyWhitelist = [.. ApiKeyWhitelist],
        BlockDurationMinutes = BlockDurationMinutes
    };
}
