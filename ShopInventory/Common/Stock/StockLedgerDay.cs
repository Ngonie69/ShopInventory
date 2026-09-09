using System.Globalization;
using ShopInventory.Services;

namespace ShopInventory.Common.Stock;

/// <summary>
/// Which day's stock snapshot is in force at a given moment.
/// </summary>
/// <remarks>
/// <para><b>Not the calendar day, in either time zone.</b> A snapshot is taken once a morning and
/// governs until the next one replaces it, so the day it belongs to rolls at the fetch time — 07:00
/// CAT — and not at any midnight. A sale rung up at 00:30 belongs to the morning that supplied the
/// figures it is selling against, which is yesterday's.</para>
///
/// <para><b>What this replaces, and why it was wrong in a way that is easy to get backwards.</b>
/// Every caller used <c>DateTime.UtcNow.Date</c>. CAT runs two hours ahead, so the UTC date rolls at
/// 02:00 CAT — five hours before the fetch that produces the day's snapshot. Between 02:00 and 07:00
/// CAT every caller therefore looked for a snapshot that did not exist yet.</para>
///
/// <para>The obvious repair — use the CAT date — is worse, not better: it moves the roll to 00:00
/// CAT and widens the same gap from five hours to seven. The 00:00–02:00 window that UTC dating gets
/// <i>right</i> is exactly the part a naive CAT conversion breaks.</para>
///
/// <para><b>What the gap costs.</b> A till reads no rows and refuses every line. Worse since the
/// stock ledger arrived: a warehouse whose snapshot cannot be found is reported as untracked, and
/// the web invoice path treats untracked warehouses as nothing to check — so during those hours the
/// ledger silently stops guarding anything at all.</para>
/// </remarks>
public static class StockLedgerDay
{
    /// <summary>Used when the configured fetch time cannot be parsed.</summary>
    public static readonly TimeSpan DefaultFetchTime = new(7, 0, 0);

    /// <summary>
    /// The snapshot day in force at <paramref name="utcNow"/>.
    /// </summary>
    /// <param name="utcNow">The current instant, in UTC.</param>
    /// <param name="fetchTimeCat">
    /// When the morning fetch runs, in CAT. Before this hour the previous day's snapshot is still
    /// the live one.
    /// </param>
    public static DateTime Resolve(DateTime utcNow, TimeSpan fetchTimeCat)
    {
        var cat = AuditService.ToCAT(utcNow);

        return cat.TimeOfDay < fetchTimeCat
            ? cat.Date.AddDays(-1)
            : cat.Date;
    }

    /// <summary>
    /// The snapshot day in force now, from the configured fetch time.
    /// </summary>
    public static DateTime Today(string? fetchTimeCat) =>
        Resolve(DateTime.UtcNow, ParseFetchTime(fetchTimeCat));

    /// <summary>
    /// Reads <c>DailyStock:StockFetchTimeCAT</c>, falling back rather than throwing.
    /// </summary>
    /// <remarks>
    /// A mistyped time must not stop the tills. The default is the value the setting itself
    /// documents, so a fallback lands on the same answer every deployment already uses.
    /// </remarks>
    public static TimeSpan ParseFetchTime(string? fetchTimeCat) =>
        TimeSpan.TryParseExact(
            fetchTimeCat?.Trim(),
            [@"hh\:mm", @"h\:mm", @"hh\:mm\:ss"],
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : DefaultFetchTime;
}
