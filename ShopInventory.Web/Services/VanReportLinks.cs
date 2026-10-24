using Microsoft.AspNetCore.Components;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// Where one van report links into another, and the query string each one reads.
/// </summary>
/// <remarks>
/// <para>
/// Every route and parameter name is written here once. A link built by hand on a page is a string
/// nothing checks, and a renamed route or parameter turns it into a page that quietly ignores what
/// it was told and shows everything instead.
/// </para>
/// <para>
/// The names are shared across the reports: <c>from</c> and <c>to</c> are trading days as
/// <c>yyyy-MM-dd</c>, <c>route</c> a route code, and <c>rep</c> a rep's user id. A page reads the
/// ones it understands and ignores the rest, so a link can carry the whole context and each report
/// takes what applies to it.
/// </para>
/// </remarks>
public static class VanReportLinks
{
    public const string From = "from";
    public const string To = "to";
    public const string Route = "route";
    public const string Rep = "rep";

    public const string ActivityPath = "/van-sales/activity";
    public const string AttendancePath = "/van-sales/attendance-report";
    public const string CompliancePath = "/van-sales/compliance";
    public const string DailyTruckPath = "/van-sales/reports/daily";
    public const string PerformancePath = "/van-sales/reports/performance";
    public const string CoveragePath = "/van-sales/reports/coverage";
    public const string InvoicesPath = "/van-sales/invoices";

    public static string Activity(DateTime from, DateTime to, Guid? rep = null) =>
        Build(ActivityPath, from, to, rep: rep);

    public static string Attendance(DateTime from, DateTime to, Guid? rep = null) =>
        Build(AttendancePath, from, to, rep: rep);

    public static string Compliance(DateTime from, DateTime to, Guid? rep = null, string? route = null) =>
        Build(CompliancePath, from, to, route, rep);

    public static string DailyTruck(DateTime from, DateTime to) =>
        Build(DailyTruckPath, from, to);

    public static string Performance(DateTime from, DateTime to, Guid? rep = null, string? route = null) =>
        Build(PerformancePath, from, to, route, rep);

    public static string Coverage(DateTime from, DateTime to, string? route = null) =>
        Build(CoveragePath, from, to, route);

    /// <summary>A route customer's own sales page over the same period.</summary>
    public static string RouteCustomer(int routeCustomerId, DateTime from, DateTime to) =>
        Build($"/route-customers/{routeCustomerId}/sales", from, to);

    /// <summary>The van invoices over the period, narrowed by the Invoices page's own search box.</summary>
    public static string Invoices(DateTime from, DateTime to, string? search = null)
    {
        var url = Build(InvoicesPath, from, to);
        return string.IsNullOrWhiteSpace(search)
            ? url
            : $"{url}&search={Uri.EscapeDataString(search.Trim())}";
    }

    /// <summary>The rep id as the links write it: no braces, no dashes.</summary>
    public static string RepKey(Guid rep) => rep.ToString("N");

    public static Guid? ParseRep(string? value) => Guid.TryParse(value, out var rep) ? rep : null;

    /// <summary>
    /// Writes the page's filters into its own URL, without a history entry, so a refresh, a bookmark
    /// or a link sent to someone opens the report as it is on screen. Null values drop the parameter.
    /// </summary>
    public static void WriteQuery(NavigationManager navigation, IReadOnlyDictionary<string, object?> values)
    {
        var target = navigation.GetUriWithQueryParameters(values);

        if (!string.Equals(target, navigation.Uri, StringComparison.Ordinal))
        {
            navigation.NavigateTo(target, replace: true);
        }
    }

    /// <summary>The period and the two shared filters, as <see cref="WriteQuery"/> takes them.</summary>
    public static Dictionary<string, object?> Query(
        DateTime? from,
        DateTime? to,
        string? route = null,
        Guid? rep = null) => new()
    {
        [From] = VanReportPeriod.FormatDay(from),
        [To] = VanReportPeriod.FormatDay(to),
        [Route] = string.IsNullOrWhiteSpace(route) ? null : route,
        [Rep] = rep is { } id ? RepKey(id) : null
    };

    private static string Build(string path, DateTime from, DateTime to, string? route = null, Guid? rep = null)
    {
        var url = $"{path}?{From}={VanReportPeriod.FormatDay(from)}&{To}={VanReportPeriod.FormatDay(to)}";

        if (!string.IsNullOrWhiteSpace(route))
        {
            url += $"&{Route}={Uri.EscapeDataString(route)}";
        }

        if (rep is { } id)
        {
            url += $"&{Rep}={RepKey(id)}";
        }

        return url;
    }
}
