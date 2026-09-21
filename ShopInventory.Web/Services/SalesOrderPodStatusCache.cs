using System.Collections.Concurrent;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// Remembers the sales-order POD answers Mobile Orders has already had, so a reload does not ask SAP
/// about all of them again.
/// </summary>
/// <remarks>
/// On 2026-09-21 every load of the page checked all 3,370 approved or fulfilled mobile orders back to
/// April: 34 <c>validate-bulk</c> calls, each a SAP SQL query, about 10 loads a day. 2,604 of those
/// orders were over 30 days old.
/// <para>
/// A Delivered answer is kept for a day, because a POD once uploaded stays. A clean "not delivered"
/// answer is kept only for an order older than <see cref="RecentOrderAge"/>. A recent order is still
/// waiting for its POD, so it is asked about on every load. An answer SAP could not give is never kept.
/// </para>
/// </remarks>
public sealed class SalesOrderPodStatusCache(TimeProvider timeProvider)
{
    public static readonly TimeSpan RecentOrderAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan DeliveredLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan OlderUndeliveredLifetime = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<int, Entry> entries = new();

    private sealed record Entry(BulkPodValidationResult Result, DateTimeOffset ExpiresAt);

    public bool TryGet(int salesOrderDocNum, out BulkPodValidationResult result)
    {
        if (entries.TryGetValue(salesOrderDocNum, out var entry))
        {
            if (entry.ExpiresAt > timeProvider.GetUtcNow())
            {
                result = entry.Result;
                return true;
            }

            entries.TryRemove(new KeyValuePair<int, Entry>(salesOrderDocNum, entry));
        }

        result = null!;
        return false;
    }

    /// <param name="result">The API's answer for one sales order.</param>
    /// <param name="orderDate">The order's date, which decides whether "not delivered" is worth keeping.</param>
    public void Remember(BulkPodValidationResult result, DateTime orderDate)
    {
        if (!result.SalesOrderDocNum.HasValue || result.LookupFailed)
            return;

        var now = timeProvider.GetUtcNow();
        TimeSpan lifetime;

        if (result.Found && result.ExistingPodCount > 0)
            lifetime = DeliveredLifetime;
        else if (now.UtcDateTime - AsUtc(orderDate) > RecentOrderAge)
            lifetime = OlderUndeliveredLifetime;
        else
            return;

        entries[result.SalesOrderDocNum.Value] = new Entry(result, now + lifetime);
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
