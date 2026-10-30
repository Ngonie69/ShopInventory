using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.RouteCustomers.Queries;
using ShopInventory.Features.VanSalesAttendance;
using ShopInventory.Features.VanSalesReports.Queries.GetAdrPerformanceReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using static ShopInventory.Features.VanSalesReports.Queries.GetAdrPerformanceReport.GetAdrPerformanceReportHandler;

namespace ShopInventory.Features.VanSalesReports.Queries.GetOrderConversionReport;

/// <summary>
/// Builds the order conversion report: every van rep's and ADR's sales orders followed to the van sale
/// each became, split by who raised it.
/// </summary>
/// <remarks>
/// The following is <see cref="AdrConversion"/>'s, unchanged — the invoice queue entry a van conversion
/// writes names the order and the sale, whoever raised the order — so an ADR's figures here are the ADR
/// report's. A van sales order is a mobile order raised by a <c>Sales</c> or <c>ADR</c> account.
/// </remarks>
public sealed class GetOrderConversionReportHandler(
    ApplicationDbContext db,
    IOptions<TaxSettings> tax
) : IRequestHandler<GetOrderConversionReportQuery, ErrorOr<OrderConversionReportResult>>
{
    /// <summary>The most orders the ledger lists. Every figure still counts the rest.</summary>
    public const int MaximumOrdersListed = 1000;

    private static readonly string[] Roles = [OrderRaisers.SalesReps, OrderRaisers.Adrs];

    public async Task<ErrorOr<OrderConversionReportResult>> Handle(
        GetOrderConversionReportQuery query,
        CancellationToken cancellationToken)
    {
        var from = query.FromDate.Date;
        var to = query.ToDate.Date;

        if (to < from)
        {
            return Error.Validation("VanSalesReports.InvalidRange", "The end of the period cannot be before its start.");
        }

        if ((to - from).TotalDays > VanSalesFacts.MaximumDays)
        {
            return Error.Validation("VanSalesReports.RangeTooWide", $"Choose a period of {VanSalesFacts.MaximumDays} days or fewer.");
        }

        var role = NormaliseRole(query.Role);
        if (query.Role is { Length: > 0 } && role is null)
        {
            return Error.Validation("VanSalesReports.UnknownRole", "Choose the sales reps (Sales) or the ADRs (ADR).");
        }

        var reps = await LoadRepsAsync(cancellationToken);
        var roleOf = reps.ToDictionary(rep => rep.Id, rep => rep.Role);
        var todayCat = AuditService.ToCAT(DateTime.UtcNow).Date;

        var period = await MeasureAsync(from, to, reps, role, query.UserId, todayCat, cancellationToken);

        var scopedReps = reps
            .Where(rep => period.ScopedRows.Contains(rep.Id))
            .Select(rep => new OrderConversionRepResult(
                rep.Id,
                rep.Username,
                rep.FullName,
                rep.Role,
                rep.AccountCode,
                rep.RouteCode,
                rep.RouteName,
                rep.IsActive,
                period.Scoped.Rows.First(row => row.UserId == rep.Id).Summary))
            .ToList();

        var raisers = Roles
            .Select(kind => new OrderConversionRaiserResult(
                kind,
                OrderRaisers.Label(kind),
                reps.Count(rep => rep.Role == kind && rep.IsActive),
                period.AllOrders.Where(order => roleOf[order.UserId] == kind).Select(order => order.UserId).Distinct().Count(),
                period.ByRole[kind]))
            .ToList();

        var outcomes = period.Scoped.Outcomes;
        var lead = period.Scoped.Overall.Money.FirstOrDefault()?.Currency;
        var products = BuildProducts(outcomes, roleOf, lead);
        var shops = BuildShops(outcomes, roleOf, lead);

        var projections = period.Scoped.Overall.Money
            .Select(money => OrderConversionInsights.Project(outcomes, money.Currency, from, to, todayCat))
            .ToList();

        var attention = OrderConversionInsights.Attention(
            outcomes, scopedReps, raisers, products, shops,
            projections.FirstOrDefault(projection => projection.Currency == lead),
            lead, todayCat);

        var salesMix = await BuildSalesMixAsync(period.PeriodSales, roleOf, cancellationToken);

        OrderConversionComparisonResult? previous = null;
        if (query.ComparePrevious)
        {
            var (previousFrom, previousTo) = AdrConversion.PreviousPeriod(from, to);
            var before = await MeasureAsync(previousFrom, previousTo, reps, role, query.UserId, todayCat, cancellationToken);
            previous = new OrderConversionComparisonResult(
                previousFrom,
                previousTo,
                before.Scoped.Overall,
                Roles.Select(kind => new OrderConversionRaiserResult(
                    kind,
                    OrderRaisers.Label(kind),
                    reps.Count(rep => rep.Role == kind && rep.IsActive),
                    before.AllOrders.Where(order => roleOf[order.UserId] == kind).Select(order => order.UserId).Distinct().Count(),
                    before.ByRole[kind])).ToList());
        }

        var ledger = period.Scoped.Orders
            .Take(MaximumOrdersListed)
            .Select(order => new OrderConversionLedgerResult(order, roleOf[order.UserId]))
            .ToList();

        return new OrderConversionReportResult(
            from,
            to,
            lead,
            period.Scoped.Overall,
            raisers,
            scopedReps
                .OrderByDescending(rep => rep.Summary.Money.FirstOrDefault(money => money.Currency == lead)?.Invoiced ?? 0m)
                .ThenBy(rep => rep.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            salesMix,
            products,
            BuildDays(outcomes, roleOf),
            shops,
            ledger,
            Math.Max(0, period.Scoped.Orders.Count - MaximumOrdersListed),
            projections,
            attention,
            previous,
            Caveats(period.Scoped, to, todayCat).ToList());
    }

    // ── Measuring one period ────────────────────────────────────────────────────

    private sealed record Measured(
        List<VanOrderFact> AllOrders,
        List<VanSaleFact> PeriodSales,
        AdrConversion.Analysis Scoped,
        HashSet<Guid> ScopedRows,
        Dictionary<string, AdrConversionSummaryResult> ByRole);

    private async Task<Measured> MeasureAsync(
        DateTime from,
        DateTime to,
        List<Rep> reps,
        string? role,
        Guid? userId,
        DateTime todayCat,
        CancellationToken cancellationToken)
    {
        var roleOf = reps.ToDictionary(rep => rep.Id, rep => rep.Role);
        var orders = await LoadOrdersAsync(db, from, to, reps.Select(rep => rep.Id).ToList(), cancellationToken);
        var sales = await VanSalesFactReader.LoadSalesAsync(db, tax.Value, new VanSalesFactFilter(from, to), cancellationToken);
        var evidence = await AdrConversion.LoadAsync(db, tax.Value, orders, cancellationToken);

        var accounts = reps.ToDictionary(rep => rep.Id, rep => rep.AccountCode);
        var routes = reps
            .Where(rep => rep.RouteCode is not null)
            .ToDictionary(rep => rep.Id, rep => new AdrAssignedRoutes.AssignedRoute(rep.RouteCode!, rep.RouteName ?? rep.RouteCode!));
        var repIds = roleOf.Keys.ToHashSet();
        var ownSales = sales.Where(sale => repIds.Contains(sale.UserId)).ToList();
        var noVisits = new Dictionary<VanSalesDayKey, HashSet<string>>();
        var noNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        AdrConversion.Analysis Build(List<VanOrderFact> subset, IReadOnlyList<Guid> rows) =>
            AdrConversion.Build(
                new AdrConversion.Input(subset, sales, ownSales, noVisits, noNames, accounts, routes, rows, rows.ToHashSet(), todayCat),
                evidence,
                null);

        var byRole = Roles.ToDictionary(
            kind => kind,
            kind => Build(orders.Where(order => roleOf[order.UserId] == kind).ToList(), []).Overall);

        var scopedOrders = orders
            .Where(order => role is null || roleOf[order.UserId] == role)
            .Where(order => userId is null || order.UserId == userId)
            .ToList();
        var rows = scopedOrders.Select(order => order.UserId).Distinct().ToList();

        return new Measured(orders, sales, Build(scopedOrders, rows), rows.ToHashSet(), byRole);
    }

    // ── Products, shops and days ────────────────────────────────────────────────

    internal static OrderConversionProductsResult BuildProducts(
        IReadOnlyList<AdrConversion.Outcome> outcomes,
        IReadOnlyDictionary<Guid, string> roleOf,
        string? lead)
    {
        var lines = outcomes
            .Where(outcome => !outcome.Order.IsWithdrawn)
            .SelectMany(outcome => outcome.Items.Select(item => (Outcome: outcome, Item: item)))
            .ToList();

        var items = lines
            .GroupBy(pair => pair.Item.ItemCode)
            .Select(group =>
            {
                var invoiced = group.Where(pair => pair.Outcome.IsInvoiced).ToList();
                return new OrderConversionItemResult(
                    group.Key,
                    group.Select(pair => pair.Item.Description).FirstOrDefault(description => description is not null),
                    group.Select(pair => pair.Item.UoMCode).FirstOrDefault(unit => unit is not null),
                    group.Select(pair => pair.Outcome.Order.Id).Distinct().Count(),
                    group.Select(pair => pair.Outcome.Order.Customer?.ToUpperInvariant() ?? VanSalesMeasures.Unattributed).Distinct().Count(),
                    group.Sum(pair => pair.Item.OrderedQuantity),
                    invoiced.Sum(pair => pair.Item.OrderedQuantity - pair.Item.ShortQuantity),
                    invoiced.Sum(pair => pair.Item.ShortQuantity),
                    group.Where(pair => !pair.Outcome.IsInvoiced).Sum(pair => pair.Item.OrderedQuantity),
                    group.Where(pair => roleOf.GetValueOrDefault(pair.Outcome.Order.UserId) == OrderRaisers.SalesReps).Sum(pair => pair.Item.OrderedQuantity),
                    group.Where(pair => roleOf.GetValueOrDefault(pair.Outcome.Order.UserId) == OrderRaisers.Adrs).Sum(pair => pair.Item.OrderedQuantity),
                    group
                        .GroupBy(pair => pair.Outcome.Order.Currency, StringComparer.OrdinalIgnoreCase)
                        .Select(currency =>
                        {
                            var sold = currency.Where(pair => pair.Outcome.IsInvoiced).ToList();
                            var shortGross = sold.Where(pair => !pair.Outcome.Unpriced).Sum(pair => pair.Item.ShortGross);
                            return new OrderConversionItemMoneyResult(
                                currency.Key,
                                decimal.Round(currency.Sum(pair => pair.Item.OrderedGross), 2),
                                decimal.Round(sold.Sum(pair => pair.Item.OrderedGross) - shortGross, 2),
                                decimal.Round(shortGross, 2));
                        })
                        .OrderByDescending(money => money.Ordered)
                        .ToList());
            })
            .OrderByDescending(item => item.Money.FirstOrDefault(money => string.Equals(money.Currency, lead, StringComparison.OrdinalIgnoreCase))?.Ordered ?? 0m)
            .ThenByDescending(item => item.Orders)
            .ThenBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new OrderConversionProductsResult(
            ProductsOrdered: items.Count,
            ProductsSupplied: items.Count(item => item.SuppliedQuantity > 0),
            ProductsShort: items.Count(item => item.ShortQuantity > 0),
            LinesOrdered: lines.Sum(pair => pair.Item.LineCount),
            UnitsOrdered: items.Sum(item => item.OrderedQuantity),
            UnitsOnInvoicedOrders: lines.Where(pair => pair.Outcome.IsInvoiced).Sum(pair => pair.Item.OrderedQuantity),
            UnitsSupplied: items.Sum(item => item.SuppliedQuantity),
            UnitsNotInvoiced: items.Sum(item => item.NotInvoicedQuantity),
            Items: items);
    }

    internal static List<OrderConversionShopResult> BuildShops(
        IReadOnlyList<AdrConversion.Outcome> outcomes,
        IReadOnlyDictionary<Guid, string> roleOf,
        string? lead) =>
        outcomes
            .Where(outcome => outcome.Order.Customer is not null)
            .GroupBy(outcome => outcome.Order.Customer!.ToUpperInvariant())
            .Select(group =>
            {
                var roles = group.Select(outcome => roleOf.GetValueOrDefault(outcome.Order.UserId)).Distinct().ToList();
                return new OrderConversionShopResult(
                    group.First().Order.Customer!,
                    group.Select(outcome => outcome.Order.CustomerName).FirstOrDefault(name => name is not null),
                    roles.Count > 1 ? "Both" : OrderRaisers.Label(roles[0] ?? OrderRaisers.SalesReps),
                    group.Count(),
                    group.Count(outcome => outcome.IsInvoiced),
                    group.Count(outcome => outcome.Kind == AdrOutcomes.ShortSupplied),
                    group.Count(outcome => outcome.Kind is AdrOutcomes.NoLinkedInvoice or AdrOutcomes.NotYetInSap),
                    group
                        .GroupBy(outcome => outcome.Order.Currency, StringComparer.OrdinalIgnoreCase)
                        .Select(currency => new AdrDayMoneyResult(
                            currency.Key,
                            currency.Sum(outcome => outcome.Ordered),
                            currency.Sum(outcome => outcome.Invoiced ?? 0m),
                            currency.Sum(outcome => outcome.Short)))
                        .OrderByDescending(money => money.Ordered)
                        .ToList(),
                    group.Max(outcome => outcome.Order.TradingDate));
            })
            .OrderByDescending(shop => shop.Money.FirstOrDefault(money => string.Equals(money.Currency, lead, StringComparison.OrdinalIgnoreCase))?.Ordered ?? 0m)
            .ThenByDescending(shop => shop.Orders)
            .ThenBy(shop => shop.CustomerCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<OrderConversionDayResult> BuildDays(
        IReadOnlyList<AdrConversion.Outcome> outcomes,
        IReadOnlyDictionary<Guid, string> roleOf) =>
        outcomes
            .GroupBy(outcome => (outcome.Order.TradingDate, Currency: outcome.Order.Currency.ToUpperInvariant()))
            .Select(group =>
            {
                var sales = group.Where(outcome => roleOf.GetValueOrDefault(outcome.Order.UserId) == OrderRaisers.SalesReps).ToList();
                var adrs = group.Where(outcome => roleOf.GetValueOrDefault(outcome.Order.UserId) == OrderRaisers.Adrs).ToList();
                return new OrderConversionDayResult(
                    group.Key.TradingDate,
                    group.First().Order.Currency,
                    group.Count(),
                    sales.Sum(outcome => outcome.Ordered),
                    sales.Sum(outcome => outcome.Invoiced ?? 0m),
                    adrs.Sum(outcome => outcome.Ordered),
                    adrs.Sum(outcome => outcome.Invoiced ?? 0m));
            })
            .OrderBy(day => day.TradingDate)
            .ThenBy(day => day.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ── The share of van sales that came from orders ────────────────────────────

    /// <summary>
    /// Every van sale in the period, against the ones a converted order became — whatever day that order
    /// was raised, so an order from last month invoiced this month counts as from an order.
    /// </summary>
    private async Task<List<OrderConversionSalesMixResult>> BuildSalesMixAsync(
        List<VanSaleFact> sales,
        IReadOnlyDictionary<Guid, string> roleOf,
        CancellationToken cancellationToken)
    {
        var references = sales.Select(sale => sale.ExternalReferenceId).Distinct(StringComparer.Ordinal).ToList();
        var raiserByReference = new Dictionary<string, Guid?>(StringComparer.Ordinal);

        foreach (var chunk in references.Chunk(2000))
        {
            var linked = await db.InvoiceQueue
                .AsNoTracking()
                .Where(entry => entry.SalesOrderId != null
                                && entry.Status != InvoiceQueueStatus.Cancelled
                                && chunk.Contains(entry.ExternalReference))
                .Join(db.SalesOrders.AsNoTracking(),
                    entry => entry.SalesOrderId,
                    order => (int?)order.Id,
                    (entry, order) => new { entry.ExternalReference, order.CreatedByUserId })
                .ToListAsync(cancellationToken);

            foreach (var link in linked)
            {
                raiserByReference.TryAdd(link.ExternalReference.Trim(), link.CreatedByUserId);
            }
        }

        string? RaiserOf(VanSaleFact sale) =>
            !raiserByReference.TryGetValue(sale.ExternalReferenceId, out var raiser) ? null
            : raiser is { } id && roleOf.TryGetValue(id, out var kind) ? kind
            : "Other";

        return sales
            .GroupBy(sale => RouteCustomerSalesReporting.NormalizeCurrency(sale.Currency), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var tagged = group.Select(sale => (Sale: sale, Raiser: RaiserOf(sale))).ToList();
                decimal Sum(string kind) => tagged.Where(pair => pair.Raiser == kind).Sum(pair => pair.Sale.TotalAmount);
                return new OrderConversionSalesMixResult(
                    group.Key,
                    group.Count(),
                    group.Sum(sale => sale.TotalAmount),
                    tagged.Count(pair => pair.Raiser is not null),
                    Sum(OrderRaisers.SalesReps),
                    Sum(OrderRaisers.Adrs),
                    Sum("Other"));
            })
            .OrderByDescending(mix => mix.VanSales)
            .ThenBy(mix => mix.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Reps ────────────────────────────────────────────────────────────────────

    private sealed record Rep(
        Guid Id,
        string Username,
        string? FullName,
        string Role,
        string? AccountCode,
        string? RouteCode,
        string? RouteName,
        bool IsActive);

    /// <summary>Every sales rep and ADR account. The role is free text, so it is compared trimmed.</summary>
    private async Task<List<Rep>> LoadRepsAsync(CancellationToken cancellationToken)
    {
        var users = await db.Users
            .AsNoTracking()
            .Select(user => new
            {
                user.Id,
                user.Username,
                user.FirstName,
                user.LastName,
                user.Role,
                user.AssignedBusinessPartnerCode,
                user.IsActive,
                RouteCode = user.Route != null ? user.Route.Code : null,
                RouteName = user.Route != null ? user.Route.Name : null
            })
            .ToListAsync(cancellationToken);

        return users
            .Select(user => (User: user, Role: NormaliseRole(user.Role)))
            .Where(pair => pair.Role is not null)
            .Select(pair =>
            {
                var fullName = $"{pair.User.FirstName} {pair.User.LastName}".Trim();
                return new Rep(
                    pair.User.Id,
                    pair.User.Username,
                    fullName.Length == 0 ? null : fullName,
                    pair.Role!,
                    string.IsNullOrWhiteSpace(pair.User.AssignedBusinessPartnerCode) ? null : pair.User.AssignedBusinessPartnerCode.Trim(),
                    pair.User.RouteCode,
                    pair.User.RouteName,
                    pair.User.IsActive);
            })
            .ToList();
    }

    private static string? NormaliseRole(string? role) => role?.Trim().ToUpperInvariant() switch
    {
        "SALES" => OrderRaisers.SalesReps,
        "ADR" => OrderRaisers.Adrs,
        _ => null
    };

    // ── Caveats ─────────────────────────────────────────────────────────────────

    private static IEnumerable<string> Caveats(AdrConversion.Analysis analysis, DateTime to, DateTime todayCat)
    {
        if (analysis.StaleWithoutInvoice > 0)
        {
            var count = analysis.StaleWithoutInvoice;
            yield return $"{count:N0} order{Plural(count)} older than two days {(count == 1 ? "has" : "have")} no invoice linked here. "
                         + "An order invoiced directly in SAP B1 cannot be matched back to the order, so it counts as not converted.";
        }

        if (analysis.RecentAwaitingInvoice > 0 && to >= todayCat.AddDays(-1))
        {
            var count = analysis.RecentAwaitingInvoice;
            yield return $"{count:N0} order{Plural(count)} from the last two days {(count == 1 ? "is" : "are")} not invoiced yet. "
                         + "Most vans invoice within a day, so the latest figures will rise; the projection allows for it.";
        }

        if (analysis.UnpricedInvoiced > 0)
        {
            var count = analysis.UnpricedInvoiced;
            yield return $"{count:N0} order{Plural(count)} {(count == 1 ? "was" : "were")} captured without prices and priced when invoiced. "
                         + "Their ordered value is taken from the invoice, so any short supply on them is not valued.";
        }

        yield return "Who raised an order is decided by the raiser's role today: a rep who moved from ADR to sales counts as a sales rep throughout.";
    }

    private static string Plural(int count) => count == 1 ? string.Empty : "s";
}
