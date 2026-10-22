using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanSalesAnalysis;

/// <summary>
/// Breaks a period's van takings down the way the desktop analysis breaks down a counter's.
/// </summary>
/// <remarks>
/// <para>
/// The breakdowns keep the desktop result's names so one page draws both, and mean the van's own thing:
/// <c>ByWarehouse</c> is the van, <c>ByBusinessPartner</c> the route customer the van sold to — never the
/// document's card, which is the van's own account on every sale it makes and is <c>ByVanAccount</c> instead
/// — <c>BySource</c> whether the
/// sale was invoiced live or uploaded offline, and <c>ByOperator</c> the rep.
/// </para>
/// <para>
/// Read in memory from the fact reader rather than grouped in SQL: the union of the two tables, their two
/// clocks and the rep attribution are solved there once, and a van estate's month is a few thousand rows.
/// </para>
/// <para>
/// VAT is the fiscal receipt's and so is known only on offline sales. An online sale's split lives on its
/// SAP invoice, so it adds nothing to <c>VatAmount</c> and its whole value to <c>NetAmount</c>.
/// </para>
/// <para>
/// Margin, when asked for, is SAP's booked gross profit on the invoice each sale was posted as — the
/// reservation's on an online sale, the desktop sale's on an offline one — by the same book the
/// management report keeps for the counters.
/// </para>
/// </remarks>
public sealed class GetVanSalesAnalysisHandler(
    ApplicationDbContext db,
    IOptions<TaxSettings> tax,
    ISaleInvoiceCostReader? costReader = null,
    ILogger<GetVanSalesAnalysisHandler>? logger = null)
    : IRequestHandler<GetVanSalesAnalysisQuery, ErrorOr<DesktopSalesAnalysisResult>>
{
    public const string OnlineKey = "online";

    public const string OfflineKey = "offline";

    /// <summary>
    /// The tenders every analysis states, even when none was taken: the three a van handset offers.
    /// </summary>
    private static readonly string[] AlwaysReported = [TenderTypes.Cash, TenderTypes.Ecocash, TenderTypes.Innbucks];

    private static readonly string[] KnownOrder =
        [TenderTypes.Cash, TenderTypes.Ecocash, TenderTypes.Innbucks, TenderTypes.Swipe];

    public async Task<ErrorOr<DesktopSalesAnalysisResult>> Handle(
        GetVanSalesAnalysisQuery query,
        CancellationToken cancellationToken)
    {
        var from = query.FromDate.Date;
        var to = query.ToDate.Date;

        if (to < from)
        {
            return Error.Validation(
                "VanSalesReports.InvalidRange",
                "The period must start on or before the day it ends.");
        }

        if ((to - from).TotalDays > VanSalesFacts.MaximumDays)
        {
            return Error.Validation(
                "VanSalesReports.RangeTooWide",
                $"Choose a period of {VanSalesFacts.MaximumDays} days or fewer.");
        }

        var days = (to - from).Days + 1;
        var previousFrom = from.AddDays(-days);
        var previousTo = from.AddDays(-1);

        var warehouse = string.IsNullOrWhiteSpace(query.WarehouseCode) ? null : query.WarehouseCode.Trim();
        var paymentMethod = string.IsNullOrWhiteSpace(query.PaymentMethod)
            ? null
            : TenderTypes.ReportingName(query.PaymentMethod);

        bool InScope(VanSaleFact sale) =>
            (warehouse is null || string.Equals(sale.WarehouseCode?.Trim(), warehouse, StringComparison.OrdinalIgnoreCase))
            && (paymentMethod is null || Tender(sale) == paymentMethod);

        var filter = new VanSalesFactFilter(from, to);
        var sales = (await VanSalesFactReader.LoadSalesAsync(db, tax.Value, filter, cancellationToken))
            .Where(InScope)
            .ToList();

        // A line belongs to the analysis when its sale does, so the van and tender filters mean the same
        // thing for the items as for the takings.
        var included = sales.Select(sale => (sale.Source, sale.ExternalReferenceId)).ToHashSet();
        var lines = (await VanSalesFactReader.LoadSaleLinesAsync(db, tax.Value, filter, cancellationToken))
            .Where(line => included.Contains((line.Source, line.ExternalReferenceId)))
            .ToList();

        var previous = (await VanSalesFactReader.LoadSalesAsync(
                db, tax.Value, new VanSalesFactFilter(previousFrom, previousTo), cancellationToken))
            .Where(InScope)
            .GroupBy(sale => CurrencyKey(sale.Currency))
            .ToDictionary(
                group => group.Key,
                group => (SalesCount: group.Count(), TotalAmount: group.Sum(sale => sale.TotalAmount)));

        ManagementSalesRollup.MarginBook? margin = null;
        ManagementMarginStatus? marginStatus = null;
        if (query.IncludeMargin)
        {
            (margin, marginStatus) = await MarginAsync(sales, cancellationToken);
        }

        var operators = await SaleOperatorNames.ResolveAsync(
            db, sales.Select(sale => sale.UserId.ToString()), cancellationToken);
        var paymentMethods = PaymentMethodColumns(sales.Select(Tender));

        var currencies = sales
            .GroupBy(sale => CurrencyKey(sale.Currency))
            .Select(group => Analyse(
                group.Key,
                group.ToList(),
                lines.Where(line => CurrencyKey(line.Currency) == group.Key).ToList(),
                paymentMethods,
                operators,
                previous.GetValueOrDefault(group.Key),
                margin))
            // US dollars first, then the rest by name — never by value, which is not comparable across them.
            .OrderBy(section => section.Currency == "USD" ? 0 : 1)
            .ThenBy(section => section.Currency, StringComparer.Ordinal)
            .ToList();

        return new DesktopSalesAnalysisResult(
            from,
            to,
            warehouse,
            SaleSourceSystems.VanSales,
            DateTime.UtcNow,
            paymentMethods,
            currencies,
            paymentMethod,
            previousFrom,
            previousTo)
        {
            Margin = marginStatus
        };
    }

    /// <summary>
    /// SAP's booked margin on the period's posted van sales, or an unavailable status — never an error.
    /// </summary>
    private async Task<(ManagementSalesRollup.MarginBook Book, ManagementMarginStatus Status)> MarginAsync(
        List<VanSaleFact> sales, CancellationToken cancellationToken)
    {
        if (costReader is null)
        {
            return (ManagementSalesRollup.MarginBook.Empty, new ManagementMarginStatus(
                false, "Gross profit is unavailable: this server has no SAP cost reader."));
        }

        // Filed as the van source on both routes, so every item lands under the vans' business.
        var posted = sales
            .Where(sale => sale.SapDocEntry is not null)
            .Select(sale => new ManagementSalesRollup.PostedSale(
                sale.SapDocEntry!.Value,
                CurrencyKey(sale.Currency),
                sale.WarehouseCode?.Trim() ?? string.Empty,
                sale.VanAccountCode,
                SaleSourceSystems.VanSales,
                sale.UserId.ToString(),
                string.Empty,
                sale.RouteCustomerId))
            .ToList();

        return await ManagementSalesRollup.MarginOfPostedAsync(
            posted,
            [],
            null,
            costReader,
            (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            GetDesktopSalesAnalysisHandler.MarginDeadline,
            cancellationToken);
    }

    private static DesktopSalesCurrencyAnalysis Analyse(
        string currency,
        List<VanSaleFact> sales,
        List<VanSaleLineFact> lines,
        IReadOnlyList<string> paymentMethods,
        IReadOnlyDictionary<Guid, string> operators,
        (int SalesCount, decimal TotalAmount) previous,
        ManagementSalesRollup.MarginBook? margin)
    {
        var salesCount = sales.Count;
        var total = sales.Sum(sale => sale.TotalAmount);
        var vat = sales.Sum(sale => sale.VatAmount ?? 0m);

        var byPaymentMethod = paymentMethods
            .Select(method =>
            {
                var paidThisWay = sales.Where(sale => Tender(sale) == method).ToList();
                var count = paidThisWay.Count;
                var value = paidThisWay.Sum(sale => sale.TotalAmount);

                return new DesktopSalesPaymentMethodRow(
                    method,
                    count,
                    value,
                    paidThisWay.Sum(sale => sale.VatAmount ?? 0m),
                    Average(value, count),
                    Share(value, total),
                    Share(count, salesCount),
                    // Only an offline sale has anywhere to record a reference, so an online one is not
                    // counted as missing one.
                    paidThisWay.Count(sale => sale.Source == VanSaleSource.OfflineBatch
                        && string.IsNullOrWhiteSpace(sale.PaymentReference)));
            })
            .ToList();

        var byDay = sales
            .GroupBy(sale => sale.TradingDate)
            .OrderBy(day => day.Key)
            .Select(day =>
            {
                var costed = margin?.ForInvoices(day.Select(sale => sale.SapDocEntry));
                return new DesktopSalesDayRow(
                    day.Key,
                    day.Count(),
                    day.Sum(sale => sale.TotalAmount),
                    day.Sum(sale => sale.VatAmount ?? 0m),
                    Split(day, paymentMethods))
                {
                    GrossProfit = costed?.GrossProfit,
                    CostedNetAmount = costed?.Revenue,
                    CostedSalesCount = costed?.CostedSales ?? 0
                };
            })
            .ToList();

        var byHour = sales
            .Where(sale => sale.SoldAtCat.HasValue)
            .GroupBy(sale => sale.SoldAtCat!.Value.Hour)
            .OrderBy(hour => hour.Key)
            .Select(hour =>
            {
                var costed = margin?.ForInvoices(hour.Select(sale => sale.SapDocEntry));
                return new DesktopSalesHourRow(hour.Key, hour.Count(), hour.Sum(sale => sale.TotalAmount))
                {
                    GrossProfit = costed?.GrossProfit,
                    CostedNetAmount = costed?.Revenue,
                    CostedSalesCount = costed?.CostedSales ?? 0
                };
            })
            .ToList();

        var items = lines
            .GroupBy(line => line.ItemCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(item => new
            {
                ItemCode = item.Key,
                ItemDescription = item.Select(line => line.ItemDescription).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)),
                Quantity = item.Sum(line => line.Quantity),
                NetAmount = item.Sum(line => line.NetLineTotal),
                SalesCount = item.Select(line => (line.Source, line.ExternalReferenceId)).Distinct().Count()
            })
            .ToList();

        var lineValue = items.Sum(item => item.NetAmount);

        var topItems = items
            .OrderByDescending(item => item.NetAmount)
            .ThenBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Take(GetDesktopSalesAnalysisHandler.TopItemCount)
            .Select(item =>
            {
                var figure = GetDesktopSalesAnalysisHandler.ItemMargin(margin, currency, SaleBusinesses.Vans, item.ItemCode);
                return new DesktopSalesItemRow(
                    item.ItemCode,
                    item.ItemDescription,
                    item.Quantity,
                    item.NetAmount,
                    item.SalesCount,
                    Share(item.NetAmount, lineValue),
                    SaleBusinesses.Vans)
                {
                    GrossProfit = figure?.GrossProfit,
                    CostedNetAmount = figure?.Revenue
                };
            })
            .ToList();

        var vansMargin = ManagementSalesRollup.MarginFigure.Sum(items.Select(item =>
            GetDesktopSalesAnalysisHandler.ItemMargin(margin, currency, SaleBusinesses.Vans, item.ItemCode)));

        // A route customer's code is unique only within the van's account, so the account is part of the
        // grouping even though only the code is shown.
        var byCustomer = Breakdown(
            sales,
            sale => sale.RouteCustomerCode is null ? "" : $"{sale.VanAccountCode}{sale.RouteCustomerCode}",
            group => group.First().RouteCustomerCode is { } code
                ? (code, CustomerName(group) ?? code)
                : ("", "No customer recorded"),
            total,
            paymentMethods,
            margin);

        return new DesktopSalesCurrencyAnalysis(
            currency,
            salesCount,
            total,
            vat,
            total - vat,
            Average(total, salesCount),
            items.Sum(item => item.Quantity),
            byDay.Count,
            items.Count,
            byPaymentMethod,
            byDay,
            byHour,
            Breakdown(
                sales,
                sale => sale.WarehouseCode?.Trim() ?? "",
                group => (group.Key, group.Key.Length == 0 ? "Not recorded" : group.Key),
                total,
                paymentMethods,
                margin),
            byCustomer,
            Breakdown(
                sales,
                sale => sale.Source == VanSaleSource.OnlineInvoice ? OnlineKey : OfflineKey,
                group => (group.Key, group.Key == OnlineKey ? "Online (invoiced live)" : "Offline (uploaded)"),
                total,
                paymentMethods,
                margin),
            Breakdown(
                sales,
                sale => sale.UserId.ToString(),
                group => (group.Key, SaleOperatorNames.Label(group.Key, operators) ?? group.Key),
                total,
                paymentMethods,
                margin),
            topItems,
            previous.SalesCount,
            previous.TotalAmount)
        {
            // Labelled by code: the partner's name is in SAP, not in either sales table, so the Web names it.
            ByVanAccount = Breakdown(
                sales,
                sale => sale.VanAccountCode?.Trim() ?? "",
                group => (group.Key, group.Key.Length == 0 ? "Not recorded" : group.Key),
                total,
                paymentMethods,
                margin),

            // The vans are one line of business, and this analysis reads nothing else.
            ItemsByBusiness = items.Count == 0
                ? []
                :
                [
                    new DesktopSalesBusinessItems(
                        SaleBusinesses.Vans,
                        SaleBusinesses.Label(SaleBusinesses.Vans),
                        items.Count,
                        items.Sum(item => item.Quantity),
                        lineValue)
                    {
                        GrossProfit = vansMargin?.GrossProfit,
                        CostedNetAmount = vansMargin?.Revenue
                    }
                ]
        };
    }

    private static List<DesktopSalesBreakdownRow> Breakdown(
        List<VanSaleFact> sales,
        Func<VanSaleFact, string> key,
        Func<IGrouping<string, VanSaleFact>, (string Key, string Label)> describe,
        decimal total,
        IReadOnlyList<string> paymentMethods,
        ManagementSalesRollup.MarginBook? margin) =>
        sales
            .GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var (rowKey, label) = describe(group);
                var value = group.Sum(sale => sale.TotalAmount);
                var costed = margin?.ForInvoices(group.Select(sale => sale.SapDocEntry));

                return new DesktopSalesBreakdownRow(
                    rowKey,
                    label,
                    group.Count(),
                    value,
                    group.Sum(sale => sale.VatAmount ?? 0m),
                    Share(value, total),
                    Split(group, paymentMethods))
                {
                    GrossProfit = costed?.GrossProfit,
                    CostedNetAmount = costed?.Revenue,
                    CostedSalesCount = costed?.CostedSales ?? 0
                };
            })
            .OrderByDescending(row => row.TotalAmount)
            .ThenBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The name most of a customer's sales carried, so a rename part-way through reads as one shop.</summary>
    private static string? CustomerName(IEnumerable<VanSaleFact> sales) =>
        sales
            .Where(sale => sale.RouteCustomerName is not null)
            .GroupBy(sale => sale.RouteCustomerName!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(name => name.Count())
            .ThenBy(name => name.Key, StringComparer.OrdinalIgnoreCase)
            .Select(name => name.Key)
            .FirstOrDefault();

    private static List<DesktopSalesPaymentAmount> Split(IEnumerable<VanSaleFact> sales, IReadOnlyList<string> paymentMethods)
    {
        var rows = sales.ToList();

        return paymentMethods
            .Select(method => new DesktopSalesPaymentAmount(
                method,
                rows.Count(sale => Tender(sale) == method),
                rows.Where(sale => Tender(sale) == method).Sum(sale => sale.TotalAmount)))
            .ToList();
    }

    /// <summary>
    /// The handset's tenders first and always, then any other that appears, and a sale with no tender last
    /// — handsets built before the payment picker send none.
    /// </summary>
    private static List<string> PaymentMethodColumns(IEnumerable<string> recorded)
    {
        var present = recorded.ToHashSet(StringComparer.Ordinal);

        var columns = KnownOrder
            .Where(method => AlwaysReported.Contains(method) || present.Contains(method))
            .Concat(present
                .Where(method => !KnownOrder.Contains(method) && method != TenderTypes.NotRecorded)
                .OrderBy(method => method, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (present.Contains(TenderTypes.NotRecorded))
        {
            columns.Add(TenderTypes.NotRecorded);
        }

        return columns;
    }

    private static string Tender(VanSaleFact sale) => TenderTypes.ReportingName(sale.PaymentMethod);

    private static string CurrencyKey(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "Not recorded" : currency.Trim().ToUpperInvariant();

    private static decimal Share(decimal part, decimal whole) =>
        whole == 0 ? 0 : Math.Round(part / whole * 100m, 1, MidpointRounding.AwayFromZero);

    private static decimal Average(decimal total, int count) =>
        count == 0 ? 0 : Math.Round(total / count, 2, MidpointRounding.AwayFromZero);
}
