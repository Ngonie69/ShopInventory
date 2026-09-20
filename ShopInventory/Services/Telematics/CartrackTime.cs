using System.Globalization;
using ShopInventory.Features.VanSalesReports.Queries;

namespace ShopInventory.Services.Telematics;

/// <summary>
/// The one place a Cartrack timestamp becomes an instant, and an instant becomes a trading date.
/// </summary>
/// <remarks>
/// <para>
/// Cartrack send <c>2026-09-18 06:52:03+02</c>: an offset, but with a space where ISO 8601 puts a
/// <c>T</c>, and a two-digit offset where it wants four. <c>DateTime.Parse</c> on that yields a
/// value with <see cref="DateTimeKind.Local"/> — correct on a machine set to CAT and silently two
/// hours out on anything else, which is the class of bug that passes every test on a developer's
/// laptop. So parsing goes through <see cref="DateTimeOffset"/> and nothing else.
/// </para>
/// <para>
/// The trading date comes from <see cref="VanSalesFacts.TradingDayOf"/>, the same function the
/// visits and the sales halves of the compliance report already use. Deliberately not a private
/// +2: telematics must not be able to disagree with the rest of the report about which day a
/// round belongs to.
/// </para>
/// <para>
/// Requests are the mirror image — <c>YYYY-MM-DD HH:MM:SS</c> with no offset, read in the
/// account's own local time. For this account that is CAT, so a CAT day is sent as itself.
/// </para>
/// </remarks>
public static class CartrackTime
{
    /// <summary>The format Cartrack's query parameters take. No offset, no <c>T</c>.</summary>
    public const string RequestFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>The shapes seen on the wire, in the order they are tried.</summary>
    private static readonly string[] ResponseFormats =
    [
        "yyyy-MM-dd HH:mm:sszzz",
        "yyyy-MM-dd HH:mm:ssz",
        "yyyy-MM-ddTHH:mm:sszzz",
        "yyyy-MM-ddTHH:mm:ssZ",
        "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz"
    ];

    private const string BareFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>
    /// Parses a Cartrack timestamp to a UTC instant, or null when it is absent or unreadable.
    /// </summary>
    public static DateTime? ToUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (DateTimeOffset.TryParseExact(
                trimmed, ResponseFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            return parsed.UtcDateTime;
        }

        // No offset on it. Read it as the account's own clock rather than the server's, which is
        // what an offsetless Cartrack timestamp means.
        if (DateTime.TryParseExact(
                trimmed, BareFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var bare))
        {
            return AuditService.FromCAT(bare);
        }

        // Last resort, still offset-aware. Anything offsetless was handled above.
        return DateTimeOffset.TryParse(
            trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose)
            ? loose.UtcDateTime
            : null;
    }

    /// <summary>
    /// The CAT calendar date an instant belongs to — the same thing the handset means by a
    /// trading date.
    /// </summary>
    public static DateTime TradingDateOf(DateTime utc) => VanSalesFacts.TradingDayOf(utc);

    /// <summary>
    /// The UTC half-open window covering one CAT trading date.
    /// </summary>
    /// <remarks>
    /// In CAT this is exactly 24 hours, which is exactly the per-call cap on the events and
    /// temperature endpoints. One trading day is therefore one request window — which is why
    /// those two are read a day at a time rather than in longer sweeps.
    /// </remarks>
    public static (DateTime FromUtc, DateTime ToUtc) UtcWindowOf(DateTime tradingDate) =>
        VanSalesFacts.ToUtcWindow(tradingDate, tradingDate);

    /// <summary>
    /// Formats a UTC instant the way a Cartrack query parameter wants it: the account's local
    /// clock, no offset.
    /// </summary>
    public static string ToRequestString(DateTime utc) =>
        AuditService.ToCAT(utc).ToString(RequestFormat, CultureInfo.InvariantCulture);

    /// <summary>Formats a bare calendar date for a <c>filter[date]</c> parameter.</summary>
    public static string ToDateString(DateTime tradingDate) =>
        tradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
