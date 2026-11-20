using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.RouteCustomers;

/// <summary>Where a route customer stands, read against an activity window.</summary>
public enum RouteCustomerActivityState
{
    /// <summary>At least one sale inside the window.</summary>
    Active,

    /// <summary>Has bought before, but not inside the window.</summary>
    Dormant,

    /// <summary>No sale on record, ever.</summary>
    NeverBought
}

/// <summary>What the Route Customers page's trading-activity filter is set to.</summary>
public enum RouteCustomerActivityFilter
{
    All,
    Active,

    /// <summary>Dormant and never bought together: everyone not buying now.</summary>
    Inactive,
    Dormant,
    NeverBought
}

/// <summary>
/// Classes route customers as active, dormant or never bought. The Route Customers page and its activity
/// workbook both read it, so the screen and the file cannot class the same shop differently.
/// </summary>
/// <remarks>
/// <para>
/// The window is the last N days counting today, the same dates the sales summary is asked for, so a
/// customer is active exactly when its sales count for the window is above zero — and the test is put on
/// the all-time last sale rather than on that count, because the count cannot tell a dormant shop from one
/// that has never bought.
/// </para>
/// <para>
/// "Dormant" is the word the Route Sales Report already uses for the same finding, so the two pages read
/// the same.
/// </para>
/// </remarks>
public static class RouteCustomerActivity
{
    /// <summary>The windows offered, in days.</summary>
    public static readonly IReadOnlyList<int> WindowOptions = [30, 60, 90, 180];

    public const int DefaultWindowDays = 90;

    /// <summary>Calendar months the workbook's "By month" sheet covers, the current one included.</summary>
    public const int HistoryMonths = 6;

    /// <summary>A window the page does not offer falls back to the default rather than to whatever was asked.</summary>
    public static int NormalizeWindow(int windowDays) =>
        WindowOptions.Contains(windowDays) ? windowDays : DefaultWindowDays;

    /// <summary>The first day of a window ending on <paramref name="today"/>, inclusive.</summary>
    public static DateTime WindowStart(DateTime today, int windowDays) => today.Date.AddDays(-(windowDays - 1));

    /// <summary>
    /// The API's dormancy threshold that matches a window. It calls a customer dormant after <em>more</em>
    /// than that many days; a window of N days ending today reaches back N − 1 days.
    /// </summary>
    public static int DormantDaysFor(int windowDays) => windowDays - 1;

    public static RouteCustomerActivityState Classify(RouteCustomerSalesRowModel customer, int windowDays) =>
        customer.LastSaleAt is null
            ? RouteCustomerActivityState.NeverBought
            : RouteSalesOverview.IsDormant(customer, DormantDaysFor(windowDays))
                ? RouteCustomerActivityState.Dormant
                : RouteCustomerActivityState.Active;

    public static bool Matches(RouteCustomerActivityState state, RouteCustomerActivityFilter filter) => filter switch
    {
        RouteCustomerActivityFilter.Active => state == RouteCustomerActivityState.Active,
        RouteCustomerActivityFilter.Inactive => state != RouteCustomerActivityState.Active,
        RouteCustomerActivityFilter.Dormant => state == RouteCustomerActivityState.Dormant,
        RouteCustomerActivityFilter.NeverBought => state == RouteCustomerActivityState.NeverBought,
        _ => true
    };

    public static string Label(RouteCustomerActivityState state) => state switch
    {
        RouteCustomerActivityState.Active => "Active",
        RouteCustomerActivityState.Dormant => "Dormant",
        _ => "Never bought"
    };

    public static string Label(RouteCustomerActivityFilter filter) => filter switch
    {
        RouteCustomerActivityFilter.Active => "Active",
        RouteCustomerActivityFilter.Inactive => "Inactive",
        RouteCustomerActivityFilter.Dormant => "Dormant",
        RouteCustomerActivityFilter.NeverBought => "Never bought",
        _ => "All customers"
    };
}

/// <summary>Which sheets the activity workbook holds.</summary>
[Flags]
public enum RouteCustomerActivitySheets
{
    None = 0,
    Summary = 1,
    Customers = 2,
    FollowUp = 4,
    NeverBought = 8,
    ByMonth = 16,
    All = Summary | Customers | FollowUp | NeverBought | ByMonth
}

/// <summary>Everything the activity workbook is laid out from.</summary>
/// <param name="Summary">The sales summary for the window, with a month history when "By month" is wanted.</param>
/// <param name="WindowDays">The activity window the customers are classed against.</param>
/// <param name="Customers">The rows to export, already cut to what was asked for.</param>
/// <param name="Details">The customer records by id, for the contact fields the summary does not carry.</param>
/// <param name="RouteLabels">"VAN008 - Van Sales West 2" by account code.</param>
/// <param name="ScopeDescription">What the rows are, in words, for the summary sheet.</param>
/// <param name="Sheets">The sheets to write.</param>
/// <param name="Today">The day the window ends on and "days since captured" is counted to.</param>
public sealed record RouteCustomerActivityReport(
    RouteCustomerSalesSummaryModel Summary,
    int WindowDays,
    IReadOnlyList<RouteCustomerSalesRowModel> Customers,
    IReadOnlyDictionary<int, RouteCustomerModel> Details,
    IReadOnlyDictionary<string, string> RouteLabels,
    string ScopeDescription,
    RouteCustomerActivitySheets Sheets,
    DateTime Today);
