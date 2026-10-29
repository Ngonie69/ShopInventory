using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.RouteCustomers;

/// <summary>
/// The Route Sales Report's overview: the five best customers and products, and one line per route. The
/// page and the detail workbook both read it, so the two cannot rank the same rows differently.
/// </summary>
/// <remarks>
/// Every money ranking is in one currency at a time. A shop that paid USD 40 and one that paid ZWG 900 have
/// no order between them, and adding the two to get one would rank them on a number that is not money.
/// Products are ranked by how often they are bought instead, as the product mix already is: value would
/// mean the same addition, and quantity would put a case above a bottle.
/// </remarks>
public static class RouteSalesOverview
{
    public const int TopCount = 5;

    /// <summary>The currencies traded, busiest first — the first is the one the page and workbook open on.</summary>
    public static IReadOnlyList<string> Currencies(RouteCustomerSalesSummaryModel summary) =>
        summary.Routes
            .SelectMany(route => route.TotalsByCurrency)
            .Where(total => !string.IsNullOrWhiteSpace(total.Currency))
            .GroupBy(total => total.Currency.Trim().ToUpperInvariant())
            .OrderByDescending(group => group.Sum(total => total.SaleCount))
            .ThenByDescending(group => group.Sum(total => total.Gross))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key)
            .ToList();

    /// <summary>A row's money in one currency, or null when it took none in it.</summary>
    public static RouteCustomerSalesTotalsModel? In(IEnumerable<RouteCustomerSalesTotalsModel> totals, string currency) =>
        Combine(totals.Where(total => string.Equals(total.Currency?.Trim(), currency, StringComparison.OrdinalIgnoreCase)), currency);

    /// <summary>The customers who took the most in <paramref name="currency"/>, largest first.</summary>
    public static IReadOnlyList<RouteSalesRankedCustomer> TopCustomers(
        IEnumerable<RouteCustomerSalesRowModel> customers,
        string currency,
        int count = TopCount)
    {
        var ranked = customers
            .Select(customer => (Customer: customer, Totals: In(customer.TotalsByCurrency, currency)))
            .Where(entry => entry.Totals is { Gross: > 0 })
            .OrderByDescending(entry => entry.Totals!.Gross)
            .ThenByDescending(entry => entry.Customer.SaleCount)
            .ThenBy(entry => entry.Customer.Name, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToList();

        var largest = ranked.Count == 0 ? 0m : ranked[0].Totals!.Gross;
        return ranked
            .Select((entry, index) => new RouteSalesRankedCustomer(index + 1, entry.Customer, entry.Totals!, Share(entry.Totals!.Gross, largest)))
            .ToList();
    }

    /// <summary>The items bought most often, as the product mix orders them.</summary>
    public static IReadOnlyList<RouteSalesRankedProduct> TopProducts(RouteCustomerProductMixModel? mix, int count = TopCount)
    {
        var ranked = Products(mix).Take(count).ToList();
        var largest = ranked.Count == 0 ? 0 : ranked[0].LineCount;
        return ranked
            .Select((item, index) => new RouteSalesRankedProduct(index + 1, item, Share(item.LineCount, largest)))
            .ToList();
    }

    /// <summary>Every item in the mix, bought most often first.</summary>
    public static IEnumerable<RouteCustomerProductMixRowModel> Products(RouteCustomerProductMixModel? mix) =>
        (mix?.Items ?? [])
            .OrderByDescending(item => item.LineCount)
            .ThenByDescending(item => item.CustomerCount)
            .ThenBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One line per route, largest in <paramref name="currency"/> first, each with its own top customers.
    /// A route that took nothing in the currency still has a line: its customers and dormancy are the point.
    /// </summary>
    public static IReadOnlyList<RouteSalesRouteLine> Routes(RouteCustomerSalesSummaryModel summary, string? currency)
    {
        var lines = summary.Routes
            .Select(route => (Route: route, Totals: currency is null ? null : In(route.TotalsByCurrency, currency)))
            .OrderByDescending(entry => entry.Totals?.Gross ?? 0m)
            .ThenByDescending(entry => entry.Route.CustomerCount)
            .ThenBy(entry => entry.Route.AssignedBusinessPartnerCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var largest = lines.Count == 0 ? 0m : lines.Max(entry => entry.Totals?.Gross ?? 0m);
        return lines
            .Select(entry => new RouteSalesRouteLine(
                entry.Route,
                entry.Totals,
                Share(entry.Totals?.Gross ?? 0m, largest),
                currency is null ? [] : TopCustomers(entry.Route.Customers, currency),
                entry.Route.Customers.Count(customer => customer.SaleCount > 0)))
            .ToList();
    }

    /// <summary>Every route's money in one currency, added up — the "All routes" line.</summary>
    public static RouteCustomerSalesTotalsModel? Total(RouteCustomerSalesSummaryModel summary, string currency) =>
        In(summary.Routes.SelectMany(route => route.TotalsByCurrency), currency);

    /// <summary>
    /// The lapsed and the never-converted, longest silence first and the never-converted last — the page's
    /// "needs a visit" list. Both are read over all time: a window cannot see a silence longer than itself.
    /// </summary>
    public static IReadOnlyList<RouteCustomerSalesRowModel> NeedsAVisit(RouteCustomerSalesSummaryModel summary) =>
        summary.Routes
            .SelectMany(route => route.Customers)
            .Where(customer => customer.LastSaleAt is null || IsDormant(customer, summary.DormantDays))
            .OrderBy(customer => customer.LastSaleAt is null)
            .ThenByDescending(customer => customer.DaysSinceLastSale ?? 0)
            .ThenBy(customer => customer.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Bought before, and not for longer than the threshold. The all-time gap decides, not the window.</summary>
    public static bool IsDormant(RouteCustomerSalesRowModel customer, int dormantDays) =>
        customer.DaysSinceLastSale is { } days && days > dormantDays;

    /// <summary>Active, Dormant or Never bought — the three standings, decided on the all-time last sale.</summary>
    public static string Standing(RouteCustomerSalesRowModel customer, int dormantDays) =>
        customer.LastSaleAt is null ? "Never bought" : IsDormant(customer, dormantDays) ? "Dormant" : "Active";

    private static decimal Share(decimal value, decimal largest) =>
        largest <= 0 || value <= 0 ? 0m : Math.Min(1m, value / largest);

    /// <summary>
    /// Adds one currency's totals. Gross profit adds only what SAP has costed and stays null when none of it
    /// is, so "not in SAP yet" is never shown as a break-even.
    /// </summary>
    private static RouteCustomerSalesTotalsModel? Combine(IEnumerable<RouteCustomerSalesTotalsModel> totals, string currency)
    {
        var list = totals.ToList();
        if (list.Count == 0)
        {
            return null;
        }

        if (list.Count == 1)
        {
            return list[0];
        }

        var costed = list.Where(total => total.GrossProfit is not null).ToList();
        var profit = costed.Count == 0 ? (decimal?)null : costed.Sum(total => total.GrossProfit!.Value);
        var net = costed.Count == 0 ? (decimal?)null : costed.Sum(total => total.CostedNetAmount ?? 0m);

        return new RouteCustomerSalesTotalsModel
        {
            Currency = currency,
            SaleCount = list.Sum(total => total.SaleCount),
            Gross = list.Sum(total => total.Gross),
            Vat = list.Sum(total => total.Vat),
            AmountPaid = list.Sum(total => total.AmountPaid),
            GrossProfit = profit,
            CostedNetAmount = net,
            CostedSaleCount = list.Sum(total => total.CostedSaleCount),
            MarginPercent = profit is { } gp && net is { } baseValue && baseValue != 0
                ? Math.Round(gp / baseValue * 100m, 2)
                : null
        };
    }
}

/// <summary>A customer's place in a top-five list, in one currency. <paramref name="Share"/> is 0–1 of the first.</summary>
public sealed record RouteSalesRankedCustomer(
    int Rank,
    RouteCustomerSalesRowModel Customer,
    RouteCustomerSalesTotalsModel Totals,
    decimal Share);

/// <summary>An item's place by times bought. <paramref name="Share"/> is 0–1 of the first.</summary>
public sealed record RouteSalesRankedProduct(
    int Rank,
    RouteCustomerProductMixRowModel Item,
    decimal Share);

/// <summary>
/// One route on the overview: its money in the chosen currency (null when it took none), its share of the
/// largest route, its top customers, and how many of its customers bought in the period.
/// </summary>
public sealed record RouteSalesRouteLine(
    RouteSalesGroupModel Route,
    RouteCustomerSalesTotalsModel? Totals,
    decimal Share,
    IReadOnlyList<RouteSalesRankedCustomer> TopCustomers,
    int TradingCount);
