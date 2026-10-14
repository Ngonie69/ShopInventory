using System.Globalization;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.Vending;

/// <summary>
/// What the filters on /vending mean: the period the sales figures are read over, and which vendors and
/// cashiers a filter keeps.
///
/// The period is the caller's "today", in CAT, and whole trading days — the same days the sales summary
/// is asked for. Nothing here reads a clock.
///
/// Activity is two questions that must not be confused. "Sold" and "no sales" are about the period: they
/// read the row's <see cref="RouteCustomerSalesRowModel.SaleCount"/>, which the summary clips to the
/// window. "Dormant" and "never sold" are about the vendor: they read
/// <see cref="RouteCustomerSalesRowModel.LastSaleAt"/>, which is all-time, because a window cannot tell a
/// vendor who has stopped from one who never started.
/// </summary>
public static class VendorListFilters
{
    public enum Period { Today, Yesterday, Week, Month, LastMonth, Quarter, Custom }

    public enum Activity { Any, Sold, Quiet, Dormant, NeverSold }

    public enum CashierState { Ready, CannotSell, Inactive }

    public const int WeekDays = 7;
    public const int QuarterDays = 90;

    /// <summary>The presets, in the order the bar offers them. <see cref="Period.Custom"/> is the two date fields.</summary>
    public static readonly (Period Period, string Label)[] Presets =
    [
        (Period.Today, "Today"),
        (Period.Yesterday, "Yesterday"),
        (Period.Week, "7 days"),
        (Period.Month, "This month"),
        (Period.LastMonth, "Last month"),
        (Period.Quarter, "90 days")
    ];

    /// <summary>
    /// The first and last day of a preset, both inclusive. A run of days ends today and counts it, so
    /// "7 days" is today and the six before — the same reading the vendor page gives its own ranges.
    /// </summary>
    public static (DateTime From, DateTime To) Window(Period period, DateTime today)
    {
        var day = today.Date;
        var monthStart = VendorSalesDigest.MonthStart(day);

        return period switch
        {
            Period.Today => (day, day),
            Period.Yesterday => (day.AddDays(-1), day.AddDays(-1)),
            Period.Week => (day.AddDays(-(WeekDays - 1)), day),
            Period.LastMonth => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            Period.Quarter => (day.AddDays(-(QuarterDays - 1)), day),
            _ => (monthStart, day)
        };
    }

    /// <summary>
    /// Keeps a typed range the right way round by moving the end the reader did not just touch. Someone
    /// moving a range later sets the start first, and for a moment it is after the old end; pulling
    /// their new start back onto that end — which is what the API does with such a pair — would undo
    /// what they typed. An emptied date is left empty: the API fills it and the page shows what it used.
    /// </summary>
    public static (DateTime? From, DateTime? To) InOrder(DateTime? from, DateTime? to, bool fromChanged)
    {
        if (from is not { } start || to is not { } end || start.Date <= end.Date)
        {
            return (from, to);
        }

        return fromChanged ? (start, start) : (end, end);
    }

    /// <summary>
    /// The heading over a figure: "Sales MTD", "Invoices today", "Sales · Sep". Short because it sits
    /// over a column and under a depot card's number; the dates themselves are in the bar.
    /// </summary>
    public static string FigureLabel(string noun, Period period, DateTime from) => period switch
    {
        Period.Today => $"{noun} today",
        Period.Yesterday => $"{noun} yesterday",
        Period.Week => $"{noun} · {WeekDays} days",
        Period.Month => $"{noun} MTD",
        Period.LastMonth => $"{noun} · {from.ToString("MMM", CultureInfo.InvariantCulture)}",
        Period.Quarter => $"{noun} · {QuarterDays} days",
        _ => $"{noun} · period"
    };

    /// <summary>
    /// Whether a vendor is one the activity filter is asking for. A vendor the summary has no row for —
    /// added since it was read — has sold nothing, in the period or ever.
    /// </summary>
    public static bool Matches(Activity activity, RouteCustomerSalesRowModel? row, DateTime today)
    {
        var saleCount = row?.SaleCount ?? 0;
        var lastSale = row?.LastSaleAt;

        return activity switch
        {
            Activity.Sold => saleCount > 0,
            Activity.Quiet => saleCount == 0,
            Activity.Dormant => lastSale is { } last && (today.Date - last.Date).TotalDays > VendorSalesDigest.DormantDays,
            Activity.NeverSold => lastSale is null,
            _ => true
        };
    }

    /// <summary>
    /// The one state a cashier's chip shows. Inactive wins over a set-up problem, as the chip has it: an
    /// account that is switched off cannot sell whatever else is wrong with it.
    /// </summary>
    public static CashierState StateOf(VendingAccountModel account) =>
        !account.IsActive ? CashierState.Inactive
        : account.SetupProblem is not null ? CashierState.CannotSell
        : CashierState.Ready;
}
