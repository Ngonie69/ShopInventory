using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;

namespace ShopInventory.Services;

/// <summary>
/// One short-lived, shared copy of the credit sweep.
/// </summary>
/// <remarks>
/// <see cref="ICreditLimitReviewService.ReviewAsync"/> reads every customer from SAP, so it is not
/// something to run per page load — and now it has two readers: the over-limit list credit control
/// works from, and the headroom shown beside an order somebody is about to approve. Both are
/// answered from the same sweep, so adding the second costs nothing.
/// <para>
/// The gate is what makes that true under load: ten people opening either screen together produce
/// one SAP sweep, not ten.
/// </para>
/// </remarks>
public interface ICreditLimitReviewCache
{
    /// <summary>
    /// The current sweep, run if the cached one has lapsed. <paramref name="refresh"/> forces a new
    /// one — the case that matters is confirming an account is back under after taking payment.
    /// </summary>
    Task<CachedCreditLimitReview> GetAsync(bool refresh, CancellationToken cancellationToken);
}

/// <summary>
/// A sweep and when it ran.
/// </summary>
/// <remarks>
/// <c>GeneratedAtUtc</c> is when SAP was actually read — cached alongside the result rather than
/// stamped on the way out. A credit decision made on a five-minute-old balance should say so, and a
/// timestamp refreshed on every read would quietly claim the answer was current. An existing test
/// caught exactly that when this cache was introduced.
/// </remarks>
public readonly record struct CachedCreditLimitReview(
    CreditLimitReview Review,
    DateTime GeneratedAtUtc,
    bool FromCache);

public sealed class CreditLimitReviewCache(
    ICreditLimitReviewService reviewService,
    IMemoryCache cache,
    IOptions<CreditLimitSettings> creditLimitSettings) : ICreditLimitReviewCache
{
    private const string CacheKey = "credit-control:review";

    // Static: handlers are resolved per request, and the whole point is one sweep across them all.
    private static readonly SemaphoreSlim SweepGate = new(1, 1);

    public async Task<CachedCreditLimitReview> GetAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        if (!refresh && cache.TryGetValue(CacheKey, out Entry cached) && cached.Review is not null)
        {
            return new CachedCreditLimitReview(cached.Review, cached.GeneratedAtUtc, FromCache: true);
        }

        await SweepGate.WaitAsync(cancellationToken);
        try
        {
            // Someone else may have swept while this request waited. Honour an explicit refresh,
            // but otherwise take their answer rather than reading all of SAP again.
            if (!refresh && cache.TryGetValue(CacheKey, out Entry sweptMeanwhile) && sweptMeanwhile.Review is not null)
            {
                return new CachedCreditLimitReview(sweptMeanwhile.Review, sweptMeanwhile.GeneratedAtUtc, FromCache: true);
            }

            var review = await reviewService.ReviewAsync(cancellationToken);
            var generatedAtUtc = DateTime.UtcNow;

            cache.Set(
                CacheKey,
                new Entry(review, generatedAtUtc),
                TimeSpan.FromMinutes(Math.Max(1, creditLimitSettings.Value.ReviewCacheMinutes)));

            return new CachedCreditLimitReview(review, generatedAtUtc, FromCache: false);
        }
        finally
        {
            SweepGate.Release();
        }
    }

    private readonly record struct Entry(CreditLimitReview? Review, DateTime GeneratedAtUtc);
}
