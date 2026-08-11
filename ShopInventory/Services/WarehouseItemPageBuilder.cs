using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Assembles one cursor page from a warehouse's ordered item codes.
/// </summary>
/// <remarks>
/// Separated from the SAP client so the two things that make a page correct can be exercised without
/// a Service Layer: that a page is positioned by cursor rather than by offset (see
/// <see cref="WarehouseItemCursor"/> for why that matters on a list that moves), and that it is
/// dense — a code resolving to no live item is consumed rather than left as a gap.
/// </remarks>
public static class WarehouseItemPageBuilder
{
    // A page fills from consecutive windows of codes because a code can resolve to nothing: only
    // live inventory items come back, so a code still carrying batch stock for an item since marked
    // invalid in SAP drops out. Left unfilled, that page comes back short — and a window of them
    // comes back empty while HasMore is still true, which reads to a caller like the end of the
    // catalogue.
    //
    // This bounds how hard one page tries. Five windows is far past anything a healthy warehouse
    // needs, and the cursor still advances over everything consumed, so the worst case is a short
    // page the caller simply asks past. A short page is recoverable where an unbounded scan of a
    // warehouse full of dead codes is not.
    public const int MaxFillWindows = 5;

    /// <summary>
    /// Takes up to <c>pageSize</c> items from <c>orderedCodes</c>, starting after <c>after</c>.
    /// </summary>
    /// <remarks>
    /// <c>resolveCodes</c> reads the live items for a window of codes and drops any that are not —
    /// which is the whole reason a page has to fill rather than simply slice.
    /// </remarks>
    public static async Task<WarehouseItemPage> BuildAsync(
        IReadOnlyList<string> orderedCodes,
        string? after,
        int pageSize,
        Func<IReadOnlyList<string>, CancellationToken, Task<List<Item>>> resolveCodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedCodes);
        ArgumentNullException.ThrowIfNull(resolveCodes);

        var normalizedPageSize = Math.Max(pageSize, 1);
        var consumed = WarehouseItemCursor.FindStart(orderedCodes, after);

        if (consumed >= orderedCodes.Count)
        {
            return new WarehouseItemPage([], false, null);
        }

        var items = new List<Item>();

        for (var window = 0;
             window < MaxFillWindows && consumed < orderedCodes.Count && items.Count < normalizedPageSize;
             window++)
        {
            var take = Math.Min(normalizedPageSize - items.Count, orderedCodes.Count - consumed);
            var windowCodes = orderedCodes.Skip(consumed).Take(take).ToList();

            items.AddRange(await resolveCodes(windowCodes, cancellationToken));
            consumed += take;
        }

        var hasMore = consumed < orderedCodes.Count;

        // The cursor names the last code consumed, not the last item returned: a code that resolved
        // to nothing has still been dealt with, and naming it here stops the next page re-reading it.
        return new WarehouseItemPage(items, hasMore, hasMore ? orderedCodes[consumed - 1] : null);
    }
}
