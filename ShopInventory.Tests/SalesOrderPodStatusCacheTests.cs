using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins which POD answers Mobile Orders keeps between loads.
/// </summary>
/// <remarks>
/// On 2026-09-21 every load of the page asked SAP about all 3,370 approved or fulfilled mobile orders:
/// 34 <c>validate-bulk</c> calls per load, 2,604 of them for orders over 30 days old.
/// </remarks>
public class SalesOrderPodStatusCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTime RecentOrder = Now.UtcDateTime.AddDays(-3);
    private static readonly DateTime OlderOrder = Now.UtcDateTime.AddDays(-45);

    private readonly StepClock clock = new(Now);
    private readonly SalesOrderPodStatusCache cache;

    public SalesOrderPodStatusCacheTests() => cache = new SalesOrderPodStatusCache(clock);

    [Fact]
    public void A_delivered_order_is_not_asked_about_again_that_day()
    {
        cache.Remember(Delivered(1), RecentOrder);

        clock.Advance(TimeSpan.FromHours(23));
        Assert.True(cache.TryGet(1, out var known));
        Assert.Equal(2, known.ExistingPodCount);

        clock.Advance(TimeSpan.FromHours(2));
        Assert.False(cache.TryGet(1, out _));
    }

    [Fact]
    public void A_recent_order_without_a_POD_is_asked_about_every_load()
    {
        cache.Remember(NotDelivered(1), RecentOrder);

        Assert.False(cache.TryGet(1, out _));
    }

    [Fact]
    public void An_older_order_without_a_POD_is_asked_about_again_after_six_hours()
    {
        cache.Remember(NotDelivered(1), OlderOrder);

        clock.Advance(TimeSpan.FromHours(5));
        Assert.True(cache.TryGet(1, out _));

        clock.Advance(TimeSpan.FromHours(2));
        Assert.False(cache.TryGet(1, out _));
    }

    [Fact]
    public void An_answer_SAP_could_not_give_is_never_kept()
    {
        cache.Remember(new BulkPodValidationResult { SalesOrderDocNum = 1, LookupFailed = true }, OlderOrder);

        Assert.False(cache.TryGet(1, out _));
    }

    [Fact]
    public void A_later_answer_replaces_an_earlier_one()
    {
        cache.Remember(NotDelivered(1), OlderOrder);
        cache.Remember(Delivered(1), OlderOrder);

        Assert.True(cache.TryGet(1, out var known));
        Assert.True(known.Found);
    }

    private static BulkPodValidationResult Delivered(int salesOrderDocNum) =>
        new() { SalesOrderDocNum = salesOrderDocNum, Found = true, ExistingPodCount = 2 };

    private static BulkPodValidationResult NotDelivered(int salesOrderDocNum) =>
        new() { SalesOrderDocNum = salesOrderDocNum, Found = true, ExistingPodCount = 0 };

    private sealed class StepClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
