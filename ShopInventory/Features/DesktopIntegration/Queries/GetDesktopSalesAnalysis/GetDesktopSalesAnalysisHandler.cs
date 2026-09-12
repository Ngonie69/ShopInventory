using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;

/// <summary>
/// Breaks a period's till takings down by how they were paid, and by when, where, who and what.
/// </summary>
/// <remarks>
/// A handful of grouped queries rather than one read of every sale. A month across every shop is tens of
/// thousands of rows, and none of the figures on this report needs a row — each is a sum over a group
/// small enough to hold. The payment-method normalisation happens after grouping, on the few distinct
/// values that come back, because legacy spellings cannot be folded in SQL without restating the rule.
/// </remarks>
public sealed class GetDesktopSalesAnalysisHandler(ApplicationDbContext db, IAuditService auditService)
    : IRequestHandler<GetDesktopSalesAnalysisQuery, ErrorOr<DesktopSalesAnalysisResult>>
{
    /// <summary>How many items the best-sellers list carries per currency.</summary>
    public const int TopItemCount = 25;

    /// <summary>
    /// The tenders every analysis states, even when none was taken.
    /// </summary>
    /// <remarks>
    /// The three a shop counter offers. A zero is information — "no EcoCash this week" is a different fact
    /// from a report that never mentions EcoCash.
    /// </remarks>
    private static readonly string[] AlwaysReported = [TenderTypes.Cash, TenderTypes.Swipe, TenderTypes.Ecocash];

    private static readonly string[] KnownOrder =
        [TenderTypes.Cash, TenderTypes.Swipe, TenderTypes.Ecocash, TenderTypes.Innbucks];

    /// <summary>
    /// Builds the analysis, and records whose takings were read.
    /// </summary>
    /// <remarks>
    /// Audited in the handler for the reason the sales list is: it shows a shop's money, so a refused read
    /// is either a client bug or somebody reaching for another shop's takings, and nothing else records it.
    /// </remarks>
    public async Task<ErrorOr<DesktopSalesAnalysisResult>> Handle(
        GetDesktopSalesAnalysisQuery request, CancellationToken cancellationToken)
    {
        var outcome = await AnalyseAsync(request, cancellationToken);

        await auditService.LogAsync(
            AuditActions.ViewDesktopSales,
            nameof(DesktopSaleEntity),
            string.IsNullOrWhiteSpace(request.WarehouseCode) ? "(caller scope)" : request.WarehouseCode.Trim(),
            outcome.IsError
                ? $"Refused a sales analysis for warehouse {request.WarehouseCode}."
                : $"Analysed {outcome.Value.Currencies.Sum(c => c.SalesCount)} sale(s) from "
                    + $"{outcome.Value.FromDate:yyyy-MM-dd} to {outcome.Value.ToDate:yyyy-MM-dd}.",
            !outcome.IsError,
            outcome.IsError ? outcome.FirstError.Description : null);

        return outcome;
    }

    private async Task<ErrorOr<DesktopSalesAnalysisResult>> AnalyseAsync(
        GetDesktopSalesAnalysisQuery request, CancellationToken cancellationToken)
    {
        var caller = await db.Users
            .AsNoTracking()
            .Include(user => user.Shop)
            .FirstOrDefaultAsync(user => user.Id == request.CallerUserId, cancellationToken);

        var scope = DesktopSalesReadScopeResolver.Resolve(caller);
        if (scope.IsError)
        {
            return scope.Errors;
        }

        var readScope = scope.Value.Narrow(request.WarehouseCode);
        if (readScope.IsError)
        {
            return readScope.Errors;
        }

        var warehouse = readScope.Value.WarehouseCode;
        var (from, to) = Period(request);
        var source = string.IsNullOrWhiteSpace(request.SourceSystem) ? null : request.SourceSystem.Trim();

        var sales = db.DesktopSales
            .AsNoTracking()
            .Where(s => s.DocDate >= from && s.DocDate <= to);

        // The same default source scope as the list. An online van sale's row carries a receipt for a sale
        // already counted as its SAP invoice, so adding it to takings would count that money twice.
        sales = source is null
            ? sales.Where(s => s.SourceSystem != SaleSourceSystems.VanSalesOnline)
            : sales.Where(s => s.SourceSystem == source);

        if (warehouse is not null)
        {
            sales = sales.Where(s => s.WarehouseCode == warehouse);
        }

        // Every breakdown except the hour and the item is a roll-up of this one grouping, so it is read
        // once. Its rows number days × shops × sources × operators × tenders at most, which stays small.
        var cells = (await sales
                .GroupBy(s => new { s.Currency, s.DocDate, s.WarehouseCode, s.SourceSystem, s.CreatedBy, s.PaymentMethod })
                .Select(g => new
                {
                    g.Key.Currency,
                    g.Key.DocDate,
                    g.Key.WarehouseCode,
                    g.Key.SourceSystem,
                    g.Key.CreatedBy,
                    g.Key.PaymentMethod,
                    SalesCount = g.Count(),
                    TotalAmount = g.Sum(s => s.TotalAmount),
                    VatAmount = g.Sum(s => s.VatAmount),
                    AmountPaid = g.Sum(s => s.AmountPaid),

                    // Per sale, not the difference of the sums: a sale nobody recorded a payment for would
                    // otherwise subtract its whole value from the change the drawer actually gave out.
                    ChangeGiven = g.Sum(s => s.AmountPaid > s.TotalAmount ? s.AmountPaid - s.TotalAmount : 0m),
                    WithoutReference = g.Count(s => s.PaymentReference == null || s.PaymentReference == ""),
                })
                .ToListAsync(cancellationToken))
            .Select(c => new Cell(
                CurrencyKey(c.Currency),
                c.DocDate.Date,
                c.WarehouseCode ?? string.Empty,
                c.SourceSystem ?? string.Empty,
                c.CreatedBy ?? string.Empty,
                TenderTypes.ReportingName(c.PaymentMethod),
                c.SalesCount,
                c.TotalAmount,
                c.VatAmount,
                c.AmountPaid,
                c.ChangeGiven,
                c.WithoutReference))
            .ToList();

        // Grouped on the UTC hour in the database and moved to the counter's clock here, where the zone is
        // known by name rather than by an offset written into a query.
        var hours = (await sales
                .GroupBy(s => new { s.Currency, s.CreatedAt.Hour })
                .Select(g => new
                {
                    g.Key.Currency,
                    g.Key.Hour,
                    SalesCount = g.Count(),
                    TotalAmount = g.Sum(s => s.TotalAmount),
                })
                .ToListAsync(cancellationToken))
            .Select(h => new HourCell(CurrencyKey(h.Currency), CatHour(h.Hour), h.SalesCount, h.TotalAmount))
            .ToList();

        var items = (await sales
                .SelectMany(s => s.Lines, (s, line) => new
                {
                    s.Currency,
                    line.SaleId,
                    line.ItemCode,
                    line.ItemDescription,
                    line.Quantity,
                    line.LineTotal,
                })
                .GroupBy(x => new { x.Currency, x.ItemCode })
                .Select(g => new
                {
                    g.Key.Currency,
                    g.Key.ItemCode,
                    ItemDescription = g.Max(x => x.ItemDescription),
                    Quantity = g.Sum(x => x.Quantity),
                    NetAmount = g.Sum(x => x.LineTotal),
                    SalesCount = g.Select(x => x.SaleId).Distinct().Count(),
                })
                .ToListAsync(cancellationToken))
            .Select(i => new ItemCell(
                CurrencyKey(i.Currency), i.ItemCode, i.ItemDescription, i.Quantity, i.NetAmount, i.SalesCount))
            .ToList();

        var operators = await OperatorNamesAsync(cells, cancellationToken);
        var paymentMethods = PaymentMethodColumns(cells.Select(c => c.PaymentMethod));

        var currencies = cells
            .GroupBy(c => c.Currency)
            .Select(group => Analyse(
                group.Key,
                group.ToList(),
                hours.Where(h => h.Currency == group.Key).ToList(),
                items.Where(i => i.Currency == group.Key).ToList(),
                paymentMethods,
                operators))
            // US dollars first — the currency the tills price in — then the rest by name. Not by
            // value: 370 ZWG and 106 USD are not comparable numbers, and ranking on them put ZiG on top.
            .OrderBy(c => c.Currency == "USD" ? 0 : 1)
            .ThenBy(c => c.Currency, StringComparer.Ordinal)
            .ToList();

        return new DesktopSalesAnalysisResult(
            from,
            to,
            warehouse,
            source,
            DateTime.UtcNow,
            paymentMethods,
            currencies);
    }

    private static DesktopSalesCurrencyAnalysis Analyse(
        string currency,
        List<Cell> cells,
        List<HourCell> hours,
        List<ItemCell> items,
        IReadOnlyList<string> paymentMethods,
        IReadOnlyDictionary<Guid, string> operators)
    {
        var salesCount = cells.Sum(c => c.SalesCount);
        var total = cells.Sum(c => c.TotalAmount);
        var vat = cells.Sum(c => c.VatAmount);

        var byPaymentMethod = paymentMethods
            .Select(method =>
            {
                var paidThisWay = cells.Where(c => c.PaymentMethod == method).ToList();
                var count = paidThisWay.Sum(c => c.SalesCount);
                var value = paidThisWay.Sum(c => c.TotalAmount);

                return new DesktopSalesPaymentMethodRow(
                    method,
                    count,
                    value,
                    paidThisWay.Sum(c => c.VatAmount),
                    paidThisWay.Sum(c => c.AmountPaid),
                    paidThisWay.Sum(c => c.ChangeGiven),
                    Average(value, count),
                    Share(value, total),
                    Share(count, salesCount),
                    paidThisWay.Sum(c => c.WithoutReference));
            })
            .ToList();

        var byDay = cells
            .GroupBy(c => c.DocDate)
            .OrderBy(day => day.Key)
            .Select(day => new DesktopSalesDayRow(
                day.Key,
                day.Sum(c => c.SalesCount),
                day.Sum(c => c.TotalAmount),
                day.Sum(c => c.VatAmount),
                Split(day, paymentMethods)))
            .ToList();

        var byHour = hours
            .GroupBy(h => h.Hour)
            .OrderBy(hour => hour.Key)
            .Select(hour => new DesktopSalesHourRow(hour.Key, hour.Sum(h => h.SalesCount), hour.Sum(h => h.TotalAmount)))
            .ToList();

        var lineValue = items.Sum(i => i.NetAmount);

        var topItems = items
            .OrderByDescending(i => i.NetAmount)
            .ThenBy(i => i.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Take(TopItemCount)
            .Select(i => new DesktopSalesItemRow(
                i.ItemCode, i.ItemDescription, i.Quantity, i.NetAmount, i.SalesCount, Share(i.NetAmount, lineValue)))
            .ToList();

        return new DesktopSalesCurrencyAnalysis(
            currency,
            salesCount,
            total,
            vat,
            total - vat,
            cells.Sum(c => c.AmountPaid),
            cells.Sum(c => c.ChangeGiven),
            Average(total, salesCount),
            items.Sum(i => i.Quantity),
            byDay.Count,
            items.Count,
            byPaymentMethod,
            byDay,
            byHour,
            Breakdown(cells, c => c.WarehouseCode, code => string.IsNullOrEmpty(code) ? "Not recorded" : code, total, paymentMethods),
            Breakdown(cells, c => c.SourceSystem, SourceLabel, total, paymentMethods),
            Breakdown(cells, c => c.CreatedBy, id => OperatorLabel(id, operators), total, paymentMethods),
            topItems);
    }

    private static List<DesktopSalesBreakdownRow> Breakdown(
        List<Cell> cells,
        Func<Cell, string> key,
        Func<string, string> label,
        decimal total,
        IReadOnlyList<string> paymentMethods) =>
        cells
            .GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DesktopSalesBreakdownRow(
                group.Key,
                label(group.Key),
                group.Sum(c => c.SalesCount),
                group.Sum(c => c.TotalAmount),
                group.Sum(c => c.VatAmount),
                Share(group.Sum(c => c.TotalAmount), total),
                Split(group, paymentMethods)))
            .OrderByDescending(row => row.TotalAmount)
            .ThenBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// A row's takings by payment method, one entry per column and zeros included, so a page can draw a
    /// matrix without having to know which cells are missing.
    /// </summary>
    private static List<DesktopSalesPaymentAmount> Split(IEnumerable<Cell> cells, IReadOnlyList<string> paymentMethods)
    {
        var rows = cells.ToList();

        return paymentMethods
            .Select(method => new DesktopSalesPaymentAmount(
                method,
                rows.Where(c => c.PaymentMethod == method).Sum(c => c.SalesCount),
                rows.Where(c => c.PaymentMethod == method).Sum(c => c.TotalAmount)))
            .ToList();
    }

    /// <summary>
    /// The payment methods the analysis is split by, in drawing order.
    /// </summary>
    /// <remarks>
    /// The counter's own tenders first and always, then any other recognised tender that appears, then
    /// historical spellings, and a sale with no tender recorded last — it is the one line that is not a
    /// way of paying.
    /// </remarks>
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

    private Task<IReadOnlyDictionary<Guid, string>> OperatorNamesAsync(
        List<Cell> cells, CancellationToken cancellationToken) =>
        SaleOperatorNames.ResolveAsync(db, cells.Select(c => c.CreatedBy), cancellationToken);

    private static string OperatorLabel(string createdBy, IReadOnlyDictionary<Guid, string> operators)
    {
        if (string.IsNullOrWhiteSpace(createdBy))
        {
            return "Not recorded";
        }

        // An account that has since been deleted keeps its id rather than being given a name it no
        // longer has: this is a breakdown, and a row with no label at all could not be read.
        return SaleOperatorNames.Label(createdBy, operators) ?? createdBy;
    }

    private static string SourceLabel(string source) => source switch
    {
        SaleSourceSystems.ShopTill => "Shop till",
        SaleSourceSystems.Vending => "Vending",
        SaleSourceSystems.VanSales => "Van sales",
        SaleSourceSystems.VanSalesOnline => "Van sales (online receipts)",
        SaleSourceSystems.LegacyDesktop => "Desktop (legacy)",
        "" => "Not recorded",
        _ => source
    };

    /// <summary>
    /// The period, with omitted ends defaulted to today on the counter's clock.
    /// </summary>
    private static (DateTime From, DateTime To) Period(GetDesktopSalesAnalysisQuery request)
    {
        var today = AuditService.ToCAT(DateTime.UtcNow).Date;
        var from = (request.FromDate ?? request.ToDate ?? today).Date;
        var to = (request.ToDate ?? (from > today ? from : today)).Date;

        return (from, to);
    }

    /// <summary>
    /// The hour on the counter's clock for a UTC hour.
    /// </summary>
    /// <remarks>
    /// Converted through the named zone rather than by adding an offset. CAT keeps no daylight saving, so
    /// converting a fixed date gives the same hour as converting each sale's own timestamp would.
    /// </remarks>
    private static int CatHour(int utcHour) =>
        AuditService.ToCAT(new DateTime(2026, 1, 1, utcHour, 0, 0, DateTimeKind.Utc)).Hour;

    private static string CurrencyKey(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "Not recorded" : currency.Trim().ToUpperInvariant();

    private static decimal Share(decimal part, decimal whole) =>
        whole == 0 ? 0 : Math.Round(part / whole * 100m, 1, MidpointRounding.AwayFromZero);

    private static decimal Average(decimal total, int count) =>
        count == 0 ? 0 : Math.Round(total / count, 2, MidpointRounding.AwayFromZero);

    private sealed record Cell(
        string Currency,
        DateTime DocDate,
        string WarehouseCode,
        string SourceSystem,
        string CreatedBy,
        string PaymentMethod,
        int SalesCount,
        decimal TotalAmount,
        decimal VatAmount,
        decimal AmountPaid,
        decimal ChangeGiven,
        int WithoutReference);

    private sealed record HourCell(string Currency, int Hour, int SalesCount, decimal TotalAmount);

    private sealed record ItemCell(
        string Currency,
        string ItemCode,
        string? ItemDescription,
        decimal Quantity,
        decimal NetAmount,
        int SalesCount);
}
