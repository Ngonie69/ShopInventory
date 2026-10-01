using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.RouteCustomers.Queries;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesReports.Queries.GetAdrPerformanceReport;

/// <summary>
/// Builds the ADR performance report: each ADR's orders and sales, and the ADRs' share of every van's.
/// </summary>
/// <remarks>
/// Sales read through <see cref="VanSalesFactReader"/>, so a rep's takings here are the same figure
/// the performance and compliance reports show.
///
/// A van sales order is a mobile order captured by a van rep — an <c>ADR</c> or <c>Sales</c> account.
/// That is the population the "all vans" side of every share is measured against. It is decided by
/// who raised the order rather than by its comment text, so it holds for orders captured before the
/// van app started writing one.
/// </remarks>
public sealed class GetAdrPerformanceReportHandler(
    ApplicationDbContext db
) : IRequestHandler<GetAdrPerformanceReportQuery, ErrorOr<AdrPerformanceReportResult>>
{
    private static readonly string[] VanRepRoles = [ApplicationRoles.Adr, ApplicationRoles.Sales];

    public async Task<ErrorOr<AdrPerformanceReportResult>> Handle(
        GetAdrPerformanceReportQuery query,
        CancellationToken cancellationToken)
    {
        var from = query.FromDate.Date;
        var to = query.ToDate.Date;

        if (to < from)
        {
            return Error.Validation(
                "VanSalesReports.InvalidRange",
                "The end of the period cannot be before its start.");
        }

        if ((to - from).TotalDays > VanSalesFacts.MaximumDays)
        {
            return Error.Validation(
                "VanSalesReports.RangeTooWide",
                $"Choose a period of {VanSalesFacts.MaximumDays} days or fewer.");
        }

        var vanReps = await db.Users
            .AsNoTracking()
            .Where(user => VanRepRoles.Contains(user.Role))
            .Select(user => new VanRep(
                user.Id,
                user.Username,
                user.FirstName,
                user.LastName,
                user.Role,
                user.AssignedBusinessPartnerCode,
                user.IsActive))
            .ToListAsync(cancellationToken);

        var vanRepIds = vanReps.Select(rep => rep.Id).ToList();
        var adrIds = vanReps
            .Where(rep => string.Equals(rep.Role, ApplicationRoles.Adr, StringComparison.OrdinalIgnoreCase))
            .Select(rep => rep.Id)
            .ToHashSet();

        var orders = await LoadOrdersAsync(from, to, vanRepIds, cancellationToken);

        // Every van's sales, not only the ADRs': the shares need the whole.
        var sales = await VanSalesFactReader.LoadSalesAsync(
            db,
            new VanSalesFactFilter(from, to),
            cancellationToken);

        var adrOrders = orders.Where(order => adrIds.Contains(order.UserId)).ToList();
        var adrSales = sales.Where(sale => adrIds.Contains(sale.UserId)).ToList();

        var overall = new AdrPerformanceOverallResult(
            AdrCount: vanReps.Count(rep => adrIds.Contains(rep.Id) && rep.IsActive),
            ActiveAdrCount: adrOrders.Select(order => order.UserId)
                .Concat(adrSales.Select(sale => sale.UserId))
                .Distinct()
                .Count(),
            OrderCounts: CountOrders(adrOrders),
            Orders: Contribution(
                OrderMoney(adrOrders),
                OrderMoney(orders)),
            Sales: Contribution(
                VanSalesMeasures.MoneyByCurrency(adrSales),
                VanSalesMeasures.MoneyByCurrency(sales)));

        var vanOrderTotals = OrderMoney(orders);
        var vanSaleTotals = VanSalesMeasures.MoneyByCurrency(sales);

        var ordersByAdr = adrOrders.ToLookup(order => order.UserId);
        var salesByAdr = adrSales.ToLookup(sale => sale.UserId);

        // An inactive ADR is listed only when the period holds something of theirs: a rep who left
        // last year is not a row of zeroes in this month's league table, but their last month is.
        var rows = vanReps
            .Where(rep => adrIds.Contains(rep.Id))
            .Where(rep => query.UserId is null || rep.Id == query.UserId)
            .Where(rep => rep.IsActive || ordersByAdr[rep.Id].Any() || salesByAdr[rep.Id].Any())
            .Select(rep => BuildRow(rep, ordersByAdr[rep.Id].ToList(), salesByAdr[rep.Id].ToList(),
                vanOrderTotals, vanSaleTotals))
            .OrderByDescending(row => row.ActiveDays > 0)
            .ThenByDescending(row => LeadGross(row.SalesTotalsByCurrency, vanSaleTotals))
            .ThenByDescending(row => LeadGross(row.OrderTotalsByCurrency, vanOrderTotals))
            .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AdrPerformanceReportResult(from, to, overall, rows);
    }

    // ── Reads ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every van rep's mobile sales orders in the period, by the CAT day they were captured.
    /// </summary>
    /// <remarks>
    /// <c>OrderDate</c> is a UTC instant, so the window is converted rather than compared as dates —
    /// otherwise every order after 22:00 CAT lands on the day before.
    /// </remarks>
    private async Task<List<VanOrderFact>> LoadOrdersAsync(
        DateTime from,
        DateTime to,
        List<Guid> vanRepIds,
        CancellationToken cancellationToken)
    {
        if (vanRepIds.Count == 0)
        {
            return [];
        }

        var (fromUtc, toUtcExclusive) = VanSalesFacts.ToUtcWindow(from, to);

        var rows = await db.SalesOrders
            .AsNoTracking()
            .Where(order => order.Source == SalesOrderSource.Mobile
                            && order.CreatedByUserId != null
                            && vanRepIds.Contains(order.CreatedByUserId.Value)
                            && order.OrderDate >= fromUtc
                            && order.OrderDate < toUtcExclusive)
            .Select(order => new
            {
                UserId = order.CreatedByUserId!.Value,
                order.OrderDate,
                order.Status,
                order.SAPDocNum,
                order.Currency,
                order.DocTotal,
                order.RouteCustomerCode
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new VanOrderFact(
                row.UserId,
                VanSalesFacts.TradingDayOf(row.OrderDate),
                row.Status,
                row.SAPDocNum is > 0,
                RouteCustomerSalesReporting.NormalizeCurrency(row.Currency),
                row.DocTotal,
                string.IsNullOrWhiteSpace(row.RouteCustomerCode) ? null : row.RouteCustomerCode.Trim()))
            .ToList();
    }

    // ── Building ────────────────────────────────────────────────────────────────

    private static AdrPerformanceRepResult BuildRow(
        VanRep rep,
        List<VanOrderFact> orders,
        List<VanSaleFact> sales,
        List<VanSalesMoneyResult> vanOrderTotals,
        List<VanSalesMoneyResult> vanSaleTotals)
    {
        var orderTotals = OrderMoney(orders);
        var saleTotals = VanSalesMeasures.MoneyByCurrency(sales);
        var fullName = $"{rep.FirstName} {rep.LastName}".Trim();

        var currencies = vanOrderTotals.Select(total => total.Currency)
            .Concat(vanSaleTotals.Select(total => total.Currency))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var shares = currencies
            .Select(currency => new AdrShareResult(
                currency,
                Share(orderTotals, vanOrderTotals, currency),
                Share(saleTotals, vanSaleTotals, currency)))
            .Where(share => share.OrderShare is not null || share.SalesShare is not null)
            .ToList();

        return new AdrPerformanceRepResult(
            UserId: rep.Id,
            Username: rep.Username,
            FullName: string.IsNullOrWhiteSpace(fullName) ? null : fullName,
            VanAccountCode: string.IsNullOrWhiteSpace(rep.AccountCode) ? null : rep.AccountCode.Trim(),
            IsActive: rep.IsActive,
            ActiveDays: orders.Select(order => order.TradingDate)
                .Concat(sales.Select(sale => sale.TradingDate))
                .Distinct()
                .Count(),
            OrderCounts: CountOrders(orders),
            OrderCustomerCount: orders
                .Where(order => order.Customer is not null)
                .Select(order => order.Customer!.ToUpperInvariant())
                .Distinct()
                .Count(),
            OrderTotalsByCurrency: orderTotals,
            SaleCustomerCount: sales
                .Where(sale => sale.RouteCustomerCode is not null)
                .Select(sale => sale.RouteCustomerCode!.ToUpperInvariant())
                .Distinct()
                .Count(),
            SalesTotalsByCurrency: saleTotals,
            Shares: shares);
    }

    /// <summary>
    /// Cancelled and rejected orders are counted but carry no money: an order that will never be
    /// delivered is not a contribution.
    /// </summary>
    private static List<VanSalesMoneyResult> OrderMoney(IEnumerable<VanOrderFact> orders) =>
        orders
            .Where(order => !order.IsWithdrawn)
            .GroupBy(order => order.Currency, StringComparer.OrdinalIgnoreCase)
            .Select(group => new VanSalesMoneyResult(
                Currency: group.Key,
                DocumentCount: group.Count(),
                DropCount: group
                    .Select(order => (order.UserId, order.TradingDate,
                        order.Customer?.ToUpperInvariant() ?? VanSalesMeasures.Unattributed))
                    .Distinct()
                    .Count(),
                Gross: group.Sum(order => order.DocTotal)))
            .OrderByDescending(total => total.Gross)
            .ThenBy(total => total.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static AdrOrderCountsResult CountOrders(IReadOnlyCollection<VanOrderFact> orders) =>
        new(
            Total: orders.Count,
            InSap: orders.Count(order => order.InSap),
            Fulfilled: orders.Count(order => order.Status is SalesOrderStatus.Fulfilled
                or SalesOrderStatus.PartiallyFulfilled),
            Pending: orders.Count(order => !order.InSap && !order.IsWithdrawn),
            Cancelled: orders.Count(order => order.IsWithdrawn));

    private static List<AdrContributionResult> Contribution(
        List<VanSalesMoneyResult> adr,
        List<VanSalesMoneyResult> all) =>
        all
            .Select(van =>
            {
                var mine = adr.FirstOrDefault(total =>
                    string.Equals(total.Currency, van.Currency, StringComparison.OrdinalIgnoreCase));

                return new AdrContributionResult(
                    van.Currency,
                    mine?.DocumentCount ?? 0,
                    mine?.Gross ?? 0m,
                    van.DocumentCount,
                    van.Gross);
            })
            .ToList();

    private static double? Share(
        List<VanSalesMoneyResult> mine,
        List<VanSalesMoneyResult> all,
        string currency)
    {
        var whole = all.FirstOrDefault(total =>
            string.Equals(total.Currency, currency, StringComparison.OrdinalIgnoreCase));

        if (whole is null || whole.Gross <= 0)
        {
            return null;
        }

        var part = mine.FirstOrDefault(total =>
            string.Equals(total.Currency, currency, StringComparison.OrdinalIgnoreCase));

        return (double)((part?.Gross ?? 0m) / whole.Gross);
    }

    /// <summary>
    /// What a row ranks on: its takings in the currency the vans took most in, so a ZiG-heavy rep
    /// is not ranked by adding ZiG to USD.
    /// </summary>
    private static decimal LeadGross(List<VanSalesMoneyResult> mine, List<VanSalesMoneyResult> all)
    {
        if (all.Count == 0)
        {
            return 0m;
        }

        return mine.FirstOrDefault(total =>
            string.Equals(total.Currency, all[0].Currency, StringComparison.OrdinalIgnoreCase))?.Gross ?? 0m;
    }

    private sealed record VanRep(
        Guid Id,
        string Username,
        string? FirstName,
        string? LastName,
        string Role,
        string? AccountCode,
        bool IsActive);

    private sealed record VanOrderFact(
        Guid UserId,
        DateTime TradingDate,
        SalesOrderStatus Status,
        bool InSap,
        string Currency,
        decimal DocTotal,
        string? Customer)
    {
        public bool IsWithdrawn => Status is SalesOrderStatus.Cancelled or SalesOrderStatus.Rejected;
    }
}
