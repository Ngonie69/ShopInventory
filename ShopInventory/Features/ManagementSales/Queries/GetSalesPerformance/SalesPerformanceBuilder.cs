using System.Globalization;
using ShopInventory.Common.Sales;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReview;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Features.VanSalesReports.Queries.GetVanSalesPerformanceReport;

namespace ShopInventory.Features.ManagementSales.Queries.GetSalesPerformance;

/// <summary>
/// Sets the Shop &amp; Vending review and the van performance report side by side, and writes the van
/// findings the review would have written had it covered the vans.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here reads a sale. Every figure comes from one of the two reports, so the email can never
/// disagree with the pages a manager opens to check it.
/// </para>
/// <para>
/// The vans are compared over the review's own previous window — the same number of days just before
/// — so all three lines of business are measured against the same days.
/// </para>
/// </remarks>
public static class SalesPerformanceBuilder
{
    /// <summary>A van route's takings falling this far on the period before is worth a look.</summary>
    public const decimal RouteDropPercent = 25m;

    /// <summary>Van takings falling this far is worth a look; twice this is worth acting on.</summary>
    public const decimal VanDropPercent = 10m;

    /// <summary>A strike rate this many points below the period before is worth a look.</summary>
    public const double StrikeRateDropPoints = 0.05;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static SalesPerformance Build(
        DesktopSalesReview review,
        VanSalesPerformanceReportResult vans,
        VanSalesPerformanceReportResult previousVans,
        int unconfirmedVanSales,
        DateTime generatedAtUtc)
    {
        var shops = Counter(review, SaleBusinesses.Shops);
        var vending = Counter(review, SaleBusinesses.Vending);
        var van = Vans(vans, previousVans, unconfirmedVanSales);

        var rows = new List<(string Business, string Label, string Currency, decimal Takings, decimal Previous, int Count, int PreviousCount, decimal? Credited)>();
        foreach (var business in review.Businesses.Where(b => SaleBusinesses.Counters.Contains(b.Business)))
        {
            foreach (var section in business.Currencies)
            {
                var h = section.Headline;
                rows.Add((business.Business, business.Label, section.Currency, h.TotalAmount, h.PreviousTotalAmount, h.SalesCount, h.PreviousSalesCount, h.CreditedAmount));
            }
        }

        var previousVanMoney = previousVans.Summary.TotalsByCurrency.ToDictionary(m => m.Currency, StringComparer.OrdinalIgnoreCase);
        foreach (var currency in van.Currencies)
        {
            var before = previousVanMoney.GetValueOrDefault(currency.Currency);
            rows.Add((SaleBusinesses.Vans, SaleBusinesses.Label(SaleBusinesses.Vans), currency.Currency,
                currency.Takings, currency.PreviousTakings, currency.Invoices, before?.DocumentCount ?? 0, null));
        }

        var byCurrency = rows
            .GroupBy(row => row.Currency, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Takings), StringComparer.OrdinalIgnoreCase);

        var scorecard = rows
            .Select(row => new SalesPerformanceScore(
                row.Business,
                row.Label,
                row.Currency,
                row.Takings,
                row.Previous,
                Change(row.Takings, row.Previous),
                row.Count,
                row.PreviousCount,
                row.Count > 0 ? Round(row.Takings / row.Count) : null,
                row.Credited,
                Share(row.Takings, byCurrency[row.Currency])))
            .ToList();

        // USD first: it is most of the trade, and a manager reads the first row as the headline.
        var totals = rows
            .GroupBy(row => row.Currency, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SalesPerformanceTotal(
                group.Key,
                group.Sum(row => row.Takings),
                group.Sum(row => row.Previous),
                Change(group.Sum(row => row.Takings), group.Sum(row => row.Previous)),
                group.Sum(row => row.Count)))
            .OrderByDescending(total => total.Takings)
            .ThenBy(total => total.Currency, StringComparer.Ordinal)
            .ToList();

        return new SalesPerformance(
            review.FromDate,
            review.ToDate,
            review.PreviousFromDate,
            review.PreviousToDate,
            generatedAtUtc,
            scorecard,
            totals,
            shops,
            vending,
            van);
    }

    private static SalesPerformanceCounter? Counter(DesktopSalesReview review, string business)
    {
        var section = review.Businesses.FirstOrDefault(b => b.Business == business);
        if (section is null)
        {
            return null;
        }

        return new SalesPerformanceCounter(
            section.Business,
            section.Label,
            section.Currencies
                .Select(currency => new SalesPerformanceCounterCurrency(
                    currency.Currency,
                    currency.Headline.TotalAmount,
                    currency.Headline.NetOfCredits,
                    currency.Headline.SalesCount,
                    currency.Headline.AverageSale,
                    currency.Headline.DaysTraded,
                    currency.Headline.MarginPercent,
                    currency.ByShop
                        .Select(shop => new SalesPerformanceOutlet(
                            shop.Label,
                            shop.Channel,
                            shop.DaysTraded,
                            shop.TotalAmount,
                            shop.PerTradingDay,
                            shop.ChangeOnPreviousPercent,
                            shop.ShareOfValuePercent,
                            shop.MarginPercent))
                        .ToList(),
                    currency.TopItems
                        .Select(item => new SalesPerformanceItem(
                            item.ItemCode,
                            item.ItemDescription,
                            item.Quantity,
                            item.NetAmount,
                            item.ShareOfNetPercent,
                            item.ChangePercent))
                        .ToList()))
                .ToList(),
            section.Findings
                .OrderBy(finding => DesktopSalesReviewSeverity.Rank(finding.Severity))
                .Select(finding => new SalesPerformanceFinding(finding.Severity, finding.Title, finding.Detail))
                .ToList());
    }

    private static SalesPerformanceVans Vans(
        VanSalesPerformanceReportResult current,
        VanSalesPerformanceReportResult previous,
        int unconfirmed)
    {
        var before = previous.Summary.TotalsByCurrency.ToDictionary(m => m.Currency, StringComparer.OrdinalIgnoreCase);
        var currencies = current.Summary.TotalsByCurrency
            .Select(money => money.Currency)
            .Concat(before.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(code =>
            {
                var now = current.Summary.TotalsByCurrency.FirstOrDefault(m => string.Equals(m.Currency, code, StringComparison.OrdinalIgnoreCase));
                var then = before.GetValueOrDefault(code);
                var takings = now?.Gross ?? 0m;
                var previousTakings = then?.Gross ?? 0m;
                return new SalesPerformanceVanCurrency(
                    code,
                    takings,
                    previousTakings,
                    Change(takings, previousTakings),
                    now?.DocumentCount ?? 0,
                    now?.AverageDropSize);
            })
            .OrderByDescending(currency => currency.Takings)
            .ThenByDescending(currency => currency.PreviousTakings)
            .ToList();

        var main = currencies.FirstOrDefault()?.Currency ?? "USD";

        var previousRoutes = previous.Routes
            .GroupBy(RouteKey)
            .ToDictionary(group => group.Key, group => group.Sum(route => Gross(route.TotalsByCurrency, main)));

        var routes = current.Routes
            .Select(route =>
            {
                var then = previousRoutes.GetValueOrDefault(RouteKey(route));
                return new SalesPerformanceRoute(
                    RouteLabel(route),
                    route.Territory,
                    route.HasRouteDay,
                    route.TradingDayCount,
                    route.TotalsByCurrency.ToDictionary(m => m.Currency, m => m.Gross, StringComparer.OrdinalIgnoreCase),
                    then > 0 ? Change(Gross(route.TotalsByCurrency, main), then) : null,
                    route.ProductiveCalls,
                    route.CallComplianceRate,
                    route.ProductiveCallRate,
                    route.KilometresTravelled);
            })
            .ToList();

        var reps = current.Reps
            .OrderByDescending(rep => Gross(rep.TotalsByCurrency, main))
            .ThenBy(rep => rep.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(rep => new SalesPerformanceRep(
                rep.DisplayName,
                rep.Routes,
                rep.TradingDayCount,
                rep.TotalsByCurrency.ToDictionary(m => m.Currency, m => m.Gross, StringComparer.OrdinalIgnoreCase),
                rep.ProductiveCalls,
                rep.CustomerCount,
                rep.StrikeRate))
            .ToList();

        var items = current.Items
            .Select(item => new SalesPerformanceVanItem(
                item.ItemCode,
                item.ItemDescription,
                item.CustomerCount,
                item.LineCount,
                item.TotalsByCurrency
                    .Where(m => string.Equals(m.Currency, main, StringComparison.OrdinalIgnoreCase))
                    .Sum(m => m.Gross)))
            .ToList();

        var findings = VanFindings(current, previous, currencies, routes, previousRoutes, unconfirmed, main);

        return new SalesPerformanceVans(
            currencies,
            main,
            current.Summary.DocumentCount,
            previous.Summary.DocumentCount,
            current.Summary.RepCount,
            current.Summary.RouteCount,
            current.Summary.TradingDayCount,
            current.Summary.Calls,
            current.Summary.ProductiveCalls,
            current.Summary.StrikeRate,
            previous.Summary.StrikeRate,
            current.Summary.CustomerCount,
            current.Summary.NewOutlets,
            current.Summary.KilometresTravelled,
            routes,
            reps,
            items,
            unconfirmed,
            findings);
    }

    /// <summary>
    /// What the van figures say, most urgent first. Written from the figures, by the same grades the
    /// Shop &amp; Vending review uses, so one email reads one way.
    /// </summary>
    private static List<SalesPerformanceFinding> VanFindings(
        VanSalesPerformanceReportResult current,
        VanSalesPerformanceReportResult previous,
        List<SalesPerformanceVanCurrency> currencies,
        List<SalesPerformanceRoute> routes,
        Dictionary<string, decimal> previousRoutes,
        int unconfirmed,
        string main)
    {
        var findings = new List<SalesPerformanceFinding>();

        foreach (var currency in currencies)
        {
            if (currency.ChangePercent is not { } change || Math.Abs(change) < VanDropPercent)
            {
                continue;
            }

            if (change < 0)
            {
                findings.Add(new SalesPerformanceFinding(
                    change <= -2 * VanDropPercent ? DesktopSalesReviewSeverity.Action : DesktopSalesReviewSeverity.Review,
                    $"Van takings down {Percent(-change)} in {currency.Currency}",
                    $"{Money(currency.Currency, currency.Takings)} against {Money(currency.Currency, currency.PreviousTakings)} in the period before, "
                        + $"on {current.Summary.DocumentCount:N0} invoice{(current.Summary.DocumentCount == 1 ? "" : "s")} against {previous.Summary.DocumentCount:N0}."));
            }
            else
            {
                findings.Add(new SalesPerformanceFinding(
                    DesktopSalesReviewSeverity.Note,
                    $"Van takings up {Percent(change)} in {currency.Currency}",
                    $"{Money(currency.Currency, currency.Takings)} against {Money(currency.Currency, currency.PreviousTakings)} in the period before."));
            }
        }

        var falling = routes
            .Where(route => route.HasRouteDay && route.ChangePercent is { } change && change <= -RouteDropPercent)
            .OrderBy(route => route.ChangePercent)
            .ToList();
        if (falling.Count > 0)
        {
            findings.Add(new SalesPerformanceFinding(
                DesktopSalesReviewSeverity.Review,
                falling.Count == 1 ? $"{falling[0].Label} fell {Percent(-falling[0].ChangePercent!.Value)}" : $"{falling.Count} routes fell by {Percent(RouteDropPercent)} or more",
                string.Join("; ", falling.Take(5).Select(route =>
                    $"{route.Label} {Money(main, route.Takings.GetValueOrDefault(main))}, down {Percent(-route.ChangePercent!.Value)} over {route.TradingDays} day{(route.TradingDays == 1 ? "" : "s")}"))
                    + "."));
        }

        var trading = current.Routes.Select(RouteKey).ToHashSet();
        var silent = previous.Routes
            .Where(route => route.HasRouteDay && !trading.Contains(RouteKey(route)) && previousRoutes.GetValueOrDefault(RouteKey(route)) > 0)
            .Select(route => (Label: RouteLabel(route), Before: previousRoutes[RouteKey(route)]))
            .OrderByDescending(route => route.Before)
            .ToList();
        if (silent.Count > 0)
        {
            findings.Add(new SalesPerformanceFinding(
                DesktopSalesReviewSeverity.Review,
                silent.Count == 1 ? $"{silent[0].Label} sold nothing this period" : $"{silent.Count} routes that traded before sold nothing this period",
                string.Join("; ", silent.Take(5).Select(route => $"{route.Label} took {Money(main, route.Before)} in the period before")) + "."));
        }

        if (current.Summary.StrikeRate is { } strike && previous.Summary.StrikeRate is { } before && before - strike >= StrikeRateDropPoints)
        {
            findings.Add(new SalesPerformanceFinding(
                DesktopSalesReviewSeverity.Review,
                $"Strike rate down to {Rate(strike)}",
                $"From {Rate(before)} in the period before: {current.Summary.PcrProductiveCalls:N0} of {current.Summary.PcrCalls:N0} calls bought."));
        }

        if (unconfirmed > 0)
        {
            findings.Add(new SalesPerformanceFinding(
                DesktopSalesReviewSeverity.Review,
                $"{unconfirmed:N0} van sale{(unconfirmed == 1 ? "" : "s")} never confirmed",
                "Their stock holds expired instead of turning into an invoice. A sale made while SAP was unavailable ends this way, and is then "
                    + "missing from every van figure here. The van exceptions report lists them."));
        }

        var coverage = current.Coverage;
        if (coverage.SalesWithoutRouteDay > 0)
        {
            findings.Add(new SalesPerformanceFinding(
                DesktopSalesReviewSeverity.Review,
                $"{coverage.SalesWithoutRouteDay:N0} van sale{(coverage.SalesWithoutRouteDay == 1 ? "" : "s")} with no Start Day",
                "The rep sold without opening a route day, so these sales count in the van totals but cannot be placed on a route."));
        }

        if (coverage.RepsWithoutVisitData > 0)
        {
            findings.Add(new SalesPerformanceFinding(
                DesktopSalesReviewSeverity.Review,
                $"{coverage.RepsWithoutVisitData:N0} rep{(coverage.RepsWithoutVisitData == 1 ? "" : "s")} sold with no check-ins",
                "Without check-ins their calls cannot be counted, so their strike rate is not measured."));
        }

        if (coverage.SalesWithoutRouteCustomer > 0)
        {
            findings.Add(new SalesPerformanceFinding(
                DesktopSalesReviewSeverity.Note,
                $"{coverage.SalesWithoutRouteCustomer:N0} van sale{(coverage.SalesWithoutRouteCustomer == 1 ? "" : "s")} not tied to a shop",
                "They count in the takings but not in outlets that bought."));
        }

        return findings
            .OrderBy(finding => DesktopSalesReviewSeverity.Rank(finding.Severity))
            .ToList();
    }

    private static string RouteKey(VanSalesRouteResult route) =>
        route.HasRouteDay ? $"R:{route.RouteCode ?? route.RouteName ?? string.Empty}" : "none";

    private static string RouteLabel(VanSalesRouteResult route) =>
        !route.HasRouteDay ? "No route day"
        : !string.IsNullOrWhiteSpace(route.RouteName) ? route.RouteName!
        : route.RouteCode ?? "Unnamed route";

    private static decimal Gross(IEnumerable<VanSalesMoneyResult> totals, string currency) =>
        totals.Where(m => string.Equals(m.Currency, currency, StringComparison.OrdinalIgnoreCase)).Sum(m => m.Gross);

    /// <summary>The change on the period before, in percent; null when there was nothing before to compare with.</summary>
    public static decimal? Change(decimal current, decimal previous) =>
        previous == 0 ? null : Math.Round((current - previous) / previous * 100m, 1, MidpointRounding.AwayFromZero);

    private static decimal Share(decimal part, decimal whole) =>
        whole == 0 ? 0 : Math.Round(part / whole * 100m, 1, MidpointRounding.AwayFromZero);

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string Money(string currency, decimal amount) => $"{currency} {amount.ToString("N2", Invariant)}";

    private static string Percent(decimal value) => $"{value.ToString("0.#", Invariant)}%";

    private static string Rate(double value) => $"{(value * 100).ToString("0.#", Invariant)}%";
}
