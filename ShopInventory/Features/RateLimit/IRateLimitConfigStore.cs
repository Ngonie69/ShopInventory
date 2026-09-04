using ShopInventory.Configuration;

namespace ShopInventory.Features.RateLimit;

/// <summary>
/// The rate limits actually in force, and the way to change them at runtime.
/// </summary>
/// <remarks>
/// <para>
/// The limits used to come from <c>IConfiguration</c> read once into a closure, and
/// <c>PUT /api/RateLimit/config</c> wrote to instance fields on a scoped service — so it answered
/// "updated successfully" and changed nothing beyond the request that called it. The limits now
/// live in <c>SystemConfigs</c>, the same place the van sales trading rules live, for the same
/// reason: shutting out a client that is hammering the API is an operational decision taken while
/// it is happening, not one worth a release and an IIS restart.
/// </para>
/// <para>
/// <see cref="Current"/> is what the ASP.NET Core limiter reads, so it is on the path of every
/// request and must never block. It answers from a snapshot refreshed at most once every
/// <see cref="RateLimitConfigStore.RefreshInterval"/>; a request that finds the snapshot stale
/// starts a refresh and is served the old values rather than waiting for the database. Limits that
/// take a few seconds to spread are not worth a database round trip per request.
/// </para>
/// </remarks>
public interface IRateLimitConfigStore
{
    /// <summary>
    /// The limits in force on this instance. Never null, never blocks, never throws.
    /// </summary>
    RateLimitSettings Current { get; }

    /// <summary>
    /// Persist new limits and apply them here immediately.
    /// </summary>
    /// <remarks>
    /// Only the fields <c>RateLimitConfigDto</c> carries are written. The queue limits and the
    /// stricter auth-endpoint limits stay deployment settings, because nothing exposes them and a
    /// half-written setting is worse than one that cannot be set at all.
    /// </remarks>
    Task UpdateAsync(RateLimitSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Load the stored limits now instead of leaving it to the next stale read.
    /// </summary>
    /// <remarks>
    /// Called once at startup. The limiter builds a client's partition once, from the limits
    /// current at that moment, so a cold start serving its first requests off the configured values
    /// would hold those clients to them until their partition was evicted - which is precisely the
    /// callers already hammering the API when somebody tightens the limit.
    /// </remarks>
    Task ReloadAsync(CancellationToken cancellationToken = default);
}
