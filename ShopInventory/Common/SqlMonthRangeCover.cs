namespace ShopInventory.Common;

/// <summary>
/// Expands a caller's date range to the whole months containing it.
/// </summary>
/// <remarks>
/// The id-range trick in <see cref="SqlIdRangeCover"/> applied to dates. A SAP SQLQueries statement
/// that embeds a user-chosen <c>from</c>/<c>to</c> is unique to that choice, and because the objects
/// cannot be deleted, every distinct range picked in the UI leaves another row behind forever.
/// Snapping to month boundaries means the statements are drawn from a set that grows by twelve a
/// year rather than by one per report run, and any date the user actually asked for is still inside
/// the queried window — callers narrow the surplus in memory.
/// </remarks>
public static class SqlMonthRangeCover
{
    /// <summary>
    /// Returns the whole months covering <paramref name="fromDate"/>..<paramref name="toDate"/>,
    /// in order. A range inside one month yields a single entry.
    /// </summary>
    public static IReadOnlyList<(DateTime Start, DateTime End)> CoverMonths(DateTime fromDate, DateTime toDate)
    {
        if (toDate.Date < fromDate.Date)
        {
            return [];
        }

        var months = new List<(DateTime Start, DateTime End)>();
        var cursor = new DateTime(fromDate.Year, fromDate.Month, 1, 0, 0, 0, fromDate.Kind);
        var last = new DateTime(toDate.Year, toDate.Month, 1, 0, 0, 0, toDate.Kind);

        while (cursor <= last)
        {
            months.Add((cursor, cursor.AddMonths(1).AddDays(-1)));
            cursor = cursor.AddMonths(1);
        }

        return months;
    }
}
