namespace ShopInventory.Services;

/// <summary>
/// Finds where a page starts within a warehouse's ordered item codes, by cursor rather than offset.
/// </summary>
/// <remarks>
/// The list this indexes into is not a stable catalogue. It is the codes holding stock in the
/// warehouse at the moment it was read — <c>OBTQ.Quantity &gt; 0</c> — so an item selling out
/// leaves it and a delivery puts one back, all day, driven by the same reps who are reading it.
/// <para>
/// Offset paging over that list silently dropped items. An item leaving before the current offset
/// shifts every later code one place left, so the next page starts one past where it should and
/// the code that moved across the boundary is never returned. Nothing errors: the pages are
/// well formed and <c>hasMore</c> stays consistent, the catalogue is just quietly short, and the
/// handset writes that short catalogue to its own store and carries it offline.
/// </para>
/// <para>
/// A cursor names the last code served instead of counting past it, so codes arriving or leaving
/// anywhere else in the list cannot move the boundary. It also holds up when the cursor's own code
/// has since left: the search lands on the first code ordered after it either way.
/// </para>
/// </remarks>
public static class WarehouseItemCursor
{
    /// <summary>
    /// The index of the first code ordered after <paramref name="after"/>, or zero to start.
    /// </summary>
    /// <remarks>
    /// Binary search under the same ordinal, case-insensitive ordering the codes were sorted with.
    /// A different comparer here would land the boundary in the wrong place on exactly the mixed-case
    /// codes it is hardest to notice.
    /// </remarks>
    public static int FindStart(IReadOnlyList<string> orderedCodes, string? after)
    {
        ArgumentNullException.ThrowIfNull(orderedCodes);

        if (string.IsNullOrWhiteSpace(after))
        {
            return 0;
        }

        var target = after.Trim();
        var low = 0;
        var high = orderedCodes.Count;

        while (low < high)
        {
            var mid = low + ((high - low) / 2);

            if (StringComparer.OrdinalIgnoreCase.Compare(orderedCodes[mid], target) <= 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}
