using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using static ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport.ManagementSalesRollup;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReview;

/// <summary>
/// Builds the business review: the till analysis and the management report for the same period, read
/// together, plus the per-shop breakdowns neither carries, turned into findings.
/// </summary>
/// <remarks>
/// <para>
/// The two reports are asked for through the mediator rather than restated, so a figure in the review is
/// the figure those pages show — same scope, same sources, same margin — and both still audit the read.
/// Each is a handful of grouped queries; the review adds four more of its own.
/// </para>
/// <para>
/// What the review adds is the view across shops that neither page takes: each shop's hours, days,
/// change and short tender, when it first traded, what its items sold for against the other shops, and
/// which of its sales were far outside its usual. The findings are then written by
/// <see cref="DesktopSalesReviewFindings"/> from those figures alone.
/// </para>
/// </remarks>
public sealed class GetDesktopSalesReviewHandler(ApplicationDbContext db, IMediator mediator)
    : IRequestHandler<GetDesktopSalesReviewQuery, ErrorOr<DesktopSalesReview>>
{
    /// <summary>How many of the best sellers the review lists.</summary>
    public const int TopItemCount = 25;

    /// <summary>A counter sale this many times its shop's average is a trade-size purchase.</summary>
    public const decimal LargeSaleFactor = 5m;

    /// <summary>The most large sales the review lists per currency.</summary>
    public const int LargeSaleLimit = 10;

    /// <summary>Items whose realised price at one shop is at least this far above another's are listed.</summary>
    public const decimal PriceSpreadPercent = DesktopSalesReviewFindings.PriceSpreadPercent;

    /// <summary>A shop must sell at least this many units of an item for its price to be compared.</summary>
    public const decimal PriceSpreadMinQuantity = 5m;

    /// <summary>How far back the first-sale search looks. Before any till existed.</summary>
    private static readonly DateTime Epoch = new(2020, 1, 1);

    public async Task<ErrorOr<DesktopSalesReview>> Handle(
        GetDesktopSalesReviewQuery request, CancellationToken cancellationToken)
    {
        var resolved = await WindowAsync(
            db, request.CallerUserId, request.FromDate, request.ToDate, request.WarehouseCode, null, null, cancellationToken);
        if (resolved.IsError)
        {
            return resolved.Errors;
        }

        var window = resolved.Value;

        var analysis = await mediator.Send(
            new GetDesktopSalesAnalysisQuery(request.CallerUserId, window.From, window.To, window.WarehouseCode),
            cancellationToken);
        if (analysis.IsError)
        {
            return analysis.Errors;
        }

        var management = await mediator.Send(
            new GetManagementSalesReportQuery(request.CallerUserId, window.From, window.To, window.WarehouseCode),
            cancellationToken);
        if (management.IsError)
        {
            return management.Errors;
        }

        var sales = Sales(db, window, window.From, window.To);
        var cells = await CellsAsync(sales, cancellationToken);
        var hours = await ShopHoursAsync(sales, cancellationToken);
        var firstSales = await FirstSalesAsync(window, cancellationToken);
        var largest = await LargestCounterSalesAsync(sales, cancellationToken);
        var walkInPartners = await WalkInPartnersAsync(cancellationToken);

        var currencies = InCurrencyOrder(
                analysis.Value.Currencies.Select(section => Section(
                    section,
                    management.Value.Currencies.FirstOrDefault(m => m.Currency == section.Currency),
                    cells.Where(c => c.Currency == section.Currency).ToList(),
                    hours.Where(h => h.Currency == section.Currency).ToList(),
                    largest.Where(s => s.Currency == section.Currency).ToList(),
                    walkInPartners,
                    firstSales,
                    window)),
                section => section.Currency)
            .ToList();

        var review = new DesktopSalesReview(
            window.From,
            window.To,
            window.PreviousFrom,
            window.PreviousTo,
            window.WarehouseCode,
            DateTime.UtcNow,
            [],
            currencies,
            management.Value.Health,
            management.Value.Margin);

        return review with
        {
            Findings = DesktopSalesReviewFindings.Write(review, management.Value)
        };
    }

    // ── Reading ────────────────────────────────────────────────────────────────────────────────────

    private static async Task<List<Cell>> CellsAsync(IQueryable<DesktopSaleEntity> sales, CancellationToken cancellationToken) =>
        (await sales
            .GroupBy(s => new { s.Currency, s.WarehouseCode, s.DocDate, s.SourceSystem, s.PaymentMethod, s.CreatedBy })
            .Select(g => new
            {
                g.Key.Currency,
                g.Key.WarehouseCode,
                g.Key.DocDate,
                g.Key.SourceSystem,
                g.Key.PaymentMethod,
                g.Key.CreatedBy,
                SalesCount = g.Count(),
                TotalAmount = g.Sum(s => s.TotalAmount),
                VatAmount = g.Sum(s => s.VatAmount),
                // Per sale: a sale taken for less than its total. One with no tender recorded is not short.
                ShortTendered = g.Sum(s => s.AmountPaid > 0 && s.AmountPaid < s.TotalAmount ? s.TotalAmount - s.AmountPaid : 0m),
            })
            .ToListAsync(cancellationToken))
        .Select(c => new Cell(
            CurrencyKey(c.Currency),
            c.WarehouseCode ?? string.Empty,
            c.DocDate.Date,
            c.SourceSystem ?? string.Empty,
            TenderTypes.ReportingName(c.PaymentMethod),
            c.CreatedBy ?? string.Empty,
            c.SalesCount,
            c.TotalAmount,
            c.VatAmount,
            c.ShortTendered))
        .ToList();

    private static async Task<List<HourCell>> ShopHoursAsync(IQueryable<DesktopSaleEntity> sales, CancellationToken cancellationToken) =>
        (await sales
            .GroupBy(s => new { s.Currency, s.WarehouseCode, s.SourceSystem, s.CreatedAt.Hour })
            .Select(g => new
            {
                g.Key.Currency,
                g.Key.WarehouseCode,
                g.Key.SourceSystem,
                g.Key.Hour,
                SalesCount = g.Count(),
                TotalAmount = g.Sum(s => s.TotalAmount),
            })
            .ToListAsync(cancellationToken))
        .Select(h => new HourCell(
            CurrencyKey(h.Currency), h.WarehouseCode ?? string.Empty, h.SourceSystem ?? string.Empty, CatHour(h.Hour), h.SalesCount, h.TotalAmount))
        .ToList();

    /// <summary>
    /// The first day each warehouse ever recorded a sale, up to the period's end, under the same scope.
    /// </summary>
    /// <remarks>
    /// So a shop that started trading mid-period is compared on the days it traded, and a first period
    /// is called one rather than read as a collapse from nothing.
    /// </remarks>
    private async Task<Dictionary<string, DateTime>> FirstSalesAsync(Window window, CancellationToken cancellationToken) =>
        (await Sales(db, window, Epoch, window.To)
            .GroupBy(s => s.WarehouseCode)
            .Select(g => new { WarehouseCode = g.Key, First = g.Min(s => s.DocDate) })
            .ToListAsync(cancellationToken))
        .GroupBy(f => f.WarehouseCode ?? string.Empty, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Min(f => f.First).Date, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The largest counter sales of the period, before they are measured against their shop's average.
    /// </summary>
    /// <remarks>
    /// Vending is left out: a settlement is a vendor's whole round paid in at once and is meant to be
    /// large. A generous slice is read so that a big shop's ordinary sales cannot crowd a small shop's
    /// outlier off the list before the comparison is made.
    /// </remarks>
    private static async Task<List<LargeCandidate>> LargestCounterSalesAsync(
        IQueryable<DesktopSaleEntity> sales, CancellationToken cancellationToken) =>
        (await sales
            .Where(s => s.SourceSystem != SaleSourceSystems.Vending)
            .OrderByDescending(s => s.TotalAmount)
            .ThenBy(s => s.Id)
            .Take(LargeSaleLimit * 10)
            .Select(s => new
            {
                s.Id,
                s.Currency,
                s.DocDate,
                s.WarehouseCode,
                s.PaymentMethod,
                s.CardCode,
                s.TotalAmount,
                Quantity = s.Lines.Sum(line => line.Quantity),
            })
            .ToListAsync(cancellationToken))
        .Select(s => new LargeCandidate(
            s.Id,
            CurrencyKey(s.Currency),
            s.DocDate.Date,
            s.WarehouseCode ?? string.Empty,
            TenderTypes.ReportingName(s.PaymentMethod),
            string.IsNullOrWhiteSpace(s.CardCode) ? null : s.CardCode.Trim(),
            s.TotalAmount,
            s.Quantity))
        .ToList();

    /// <summary>
    /// The business partner each shop's walk-in sales are booked to, by warehouse.
    /// </summary>
    /// <remarks>
    /// A till sale always carries a card code — the shop's own partner when nobody was named. A sale on
    /// any other code was made to a known customer, which is what a large sale is measured against.
    /// </remarks>
    private async Task<Dictionary<string, HashSet<string>>> WalkInPartnersAsync(CancellationToken cancellationToken) =>
        (await db.Shops.AsNoTracking()
            .Where(shop => shop.WarehouseCode != null && shop.BusinessPartnerCode != null)
            .Select(shop => new { shop.WarehouseCode, shop.BusinessPartnerCode })
            .ToListAsync(cancellationToken))
        .GroupBy(shop => shop.WarehouseCode!.Trim(), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            g => g.Key,
            g => g.Select(shop => shop.BusinessPartnerCode!.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    // ── Building ───────────────────────────────────────────────────────────────────────────────────

    private static DesktopSalesReviewCurrency Section(
        DesktopSalesCurrencyAnalysis analysis,
        ManagementCurrencySection? management,
        List<Cell> cells,
        List<HourCell> hours,
        List<LargeCandidate> largest,
        IReadOnlyDictionary<string, HashSet<string>> walkInPartners,
        IReadOnlyDictionary<string, DateTime> firstSales,
        Window window)
    {
        var currency = analysis.Currency;
        var matrix = management?.ItemDepotMatrix ?? [];
        var depots = (management?.ByDepot ?? [])
            .ToDictionary(row => row.Key, StringComparer.OrdinalIgnoreCase);

        var shops = cells
            .GroupBy(c => c.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => Shop(group.Key, group.ToList(), matrix, depots.GetValueOrDefault(group.Key), firstSales, window, analysis.TotalAmount))
            .OrderByDescending(shop => shop.TotalAmount)
            .ThenBy(shop => shop.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byHour = hours
            .GroupBy(h => h.Hour)
            .OrderBy(g => g.Key)
            .Select(g => new DesktopSalesReviewHourRow(
                g.Key,
                g.Sum(h => h.SalesCount),
                g.Sum(h => h.TotalAmount),
                g.Where(h => h.SourceSystem == SaleSourceSystems.Vending).Sum(h => h.SalesCount),
                g.Where(h => h.SourceSystem == SaleSourceSystems.Vending).Sum(h => h.TotalAmount)))
            .ToList();

        var shopHours = hours
            .GroupBy(h => new { h.WarehouseCode, h.Hour })
            .Select(g => new DesktopSalesReviewShopHourCell(g.Key.WarehouseCode, g.Key.Hour, g.Sum(h => h.SalesCount), g.Sum(h => h.TotalAmount)))
            .OrderBy(c => c.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Hour)
            .ToList();

        var shopDays = cells
            .GroupBy(c => new { c.WarehouseCode, c.DocDate })
            .Select(g => new DesktopSalesReviewShopDayCell(g.Key.WarehouseCode, g.Key.DocDate, g.Sum(c => c.SalesCount), g.Sum(c => c.TotalAmount)))
            .OrderBy(c => c.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Date)
            .ToList();

        var items = management?.ByItem ?? [];
        var lineValue = items.Sum(i => i.NetAmount);
        var topItems = TopItems(items, lineValue);

        var summary = management?.Summary;
        var headline = new DesktopSalesReviewHeadline(
            analysis.SalesCount,
            analysis.TotalAmount,
            analysis.NetAmount,
            analysis.VatAmount,
            analysis.AverageSale,
            analysis.QuantitySold,
            analysis.DistinctItems,
            analysis.DaysTraded,
            cells.Sum(c => c.ShortTendered),
            analysis.PreviousSalesCount,
            analysis.PreviousTotalAmount,
            Average(analysis.PreviousTotalAmount, analysis.PreviousSalesCount),
            Change(analysis.TotalAmount, analysis.PreviousTotalAmount),
            analysis.PreviousSalesCount == 0 ? null : Round((analysis.SalesCount - analysis.PreviousSalesCount) * PreciseAverage(analysis.PreviousTotalAmount, analysis.PreviousSalesCount)),
            analysis.PreviousSalesCount == 0 ? null : Round(analysis.SalesCount * (PreciseAverage(analysis.TotalAmount, analysis.SalesCount) - PreciseAverage(analysis.PreviousTotalAmount, analysis.PreviousSalesCount))),
            Percent(analysis.VatAmount, analysis.NetAmount, 2),
            summary?.GrossProfit,
            summary?.MarginPercent,
            summary?.CostedNetAmount ?? 0m);

        return new DesktopSalesReviewCurrency(
            currency,
            headline,
            analysis.ByDay,
            byHour,
            analysis.ByPaymentMethod,
            management?.ByChannel ?? [],
            shops,
            shopHours,
            shopDays,
            topItems,
            items.Count,
            lineValue - topItems.Sum(i => i.NetAmount),
            PriceSpreads(items, matrix, shops),
            LargeSales(largest, shops, walkInPartners));
    }

    private static DesktopSalesReviewShopRow Shop(
        string warehouse,
        List<Cell> cells,
        List<ManagementItemDepotCell> matrix,
        ManagementBreakdownRow? depot,
        IReadOnlyDictionary<string, DateTime> firstSales,
        Window window,
        decimal currencyTotal)
    {
        var count = cells.Sum(c => c.SalesCount);
        var total = cells.Sum(c => c.TotalAmount);
        var vat = cells.Sum(c => c.VatAmount);
        var net = total - vat;
        var daysTraded = cells.Select(c => c.DocDate).Distinct().Count();
        var cash = cells.Where(c => c.PaymentMethod == TenderTypes.ReportingName(TenderTypes.Cash)).Sum(c => c.TotalAmount);
        var items = matrix.Where(m => string.Equals(m.WarehouseCode, warehouse, StringComparison.OrdinalIgnoreCase)).ToList();
        var quantity = items.Sum(m => m.Quantity);
        var first = firstSales.TryGetValue(warehouse, out var date) ? date : (DateTime?)null;

        var channel = cells
            .GroupBy(c => c.SourceSystem)
            .OrderByDescending(g => g.Sum(c => c.SalesCount))
            .Select(g => SourceLabel(g.Key))
            .FirstOrDefault() ?? NotRecorded;

        return new DesktopSalesReviewShopRow(
            warehouse,
            depot?.Label ?? (warehouse.Length == 0 ? NotRecorded : warehouse),
            depot?.Hint,
            channel,
            cells.Select(c => c.CreatedBy).Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            first,
            first is { } started && started >= window.From && started <= window.To,
            daysTraded,
            count,
            total,
            net,
            vat,
            quantity,
            items.Count,
            Share(total, currencyTotal),
            Average(total, count),
            daysTraded == 0 ? 0 : Round(total / daysTraded),
            count == 0 ? 0 : Math.Round(quantity / count, 1, MidpointRounding.AwayFromZero),
            PerUnit(items.Sum(m => m.NetAmount), quantity),
            cells.Sum(c => c.ShortTendered),
            cash,
            Percent(total - cash, total, 1),
            Percent(vat, net, 2),
            depot?.PreviousTotalAmount ?? 0m,
            depot?.ChangePercent,
            depot?.GrossProfit,
            depot?.MarginPercent);
    }

    private static List<DesktopSalesReviewItemRow> TopItems(List<ManagementItemRow> items, decimal lineValue)
    {
        var cumulative = 0m;
        return items
            .OrderByDescending(i => i.NetAmount)
            .ThenBy(i => i.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Take(TopItemCount)
            .Select(i =>
            {
                cumulative += i.NetAmount;
                var cumulativeShare = Percent(cumulative, lineValue, 1);
                return new DesktopSalesReviewItemRow(
                    i.ItemCode,
                    i.ItemDescription,
                    i.Quantity,
                    i.NetAmount,
                    i.SalesCount,
                    i.ShareOfNetPercent,
                    cumulativeShare,
                    cumulativeShare <= 80m ? "A" : cumulativeShare <= 95m ? "B" : "C",
                    i.UnitsPerSale,
                    i.AverageUnitPrice,
                    i.DiscountAmount,
                    i.ChangePercent,
                    i.GrossProfit,
                    i.MarginPercent);
            })
            .ToList();
    }

    /// <summary>
    /// Items that sold for noticeably different prices at different shops, biggest money first.
    /// </summary>
    /// <remarks>
    /// The realised price — value over quantity — at each shop that sold enough of it to say, compared
    /// only with shops of the same channel: a vendor buys at a trade price by design, and setting that
    /// against a counter's retail price would bury the differences that are questions. A blended price
    /// can also hide a mix of full-price and discounted lines, which is why the review asks rather than
    /// calling it an error.
    /// </remarks>
    private static List<DesktopSalesReviewPriceSpread> PriceSpreads(
        List<ManagementItemRow> items, List<ManagementItemDepotCell> matrix, List<DesktopSalesReviewShopRow> shops)
    {
        var descriptions = items.ToDictionary(i => i.ItemCode, i => i.ItemDescription, StringComparer.OrdinalIgnoreCase);
        var channels = shops.ToDictionary(s => s.WarehouseCode, s => s.Channel, StringComparer.OrdinalIgnoreCase);

        return matrix
            .Where(m => m.Quantity >= PriceSpreadMinQuantity && m.NetAmount > 0 && m.WarehouseCode.Length > 0)
            .GroupBy(m => (Item: m.ItemCode.ToUpperInvariant(), Channel: channels.GetValueOrDefault(m.WarehouseCode, NotRecorded)))
            .Where(g => g.Count() >= 2)
            .Select(g =>
            {
                var priced = g.Select(m => (m.WarehouseCode, m.Quantity, Price: m.NetAmount / m.Quantity)).ToList();
                var low = priced.MinBy(p => p.Price);
                var high = priced.MaxBy(p => p.Price);
                var spread = Math.Round((high.Price - low.Price) / low.Price * 100m, 1, MidpointRounding.AwayFromZero);
                var itemCode = g.First().ItemCode;
                return new DesktopSalesReviewPriceSpread(
                    itemCode,
                    descriptions.GetValueOrDefault(itemCode),
                    priced.Count,
                    low.WarehouseCode,
                    Math.Round(low.Price, 4, MidpointRounding.AwayFromZero),
                    low.Quantity,
                    high.WarehouseCode,
                    Math.Round(high.Price, 4, MidpointRounding.AwayFromZero),
                    spread,
                    Round((high.Price - low.Price) * low.Quantity));
            })
            .Where(s => s.SpreadPercent >= PriceSpreadPercent)
            .OrderByDescending(s => s.UpliftAtHighPrice)
            .ThenBy(s => s.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<DesktopSalesReviewLargeSale> LargeSales(
        List<LargeCandidate> candidates,
        List<DesktopSalesReviewShopRow> shops,
        IReadOnlyDictionary<string, HashSet<string>> walkInPartners)
    {
        var averages = shops.ToDictionary(s => s.WarehouseCode, s => s.AverageSale, StringComparer.OrdinalIgnoreCase);

        return candidates
            .Select(c => (Sale: c, Average: averages.GetValueOrDefault(c.WarehouseCode)))
            .Where(x => x.Average > 0 && x.Sale.TotalAmount >= x.Average * LargeSaleFactor)
            .OrderByDescending(x => x.Sale.TotalAmount / x.Average)
            .Take(LargeSaleLimit)
            .Select(x => new DesktopSalesReviewLargeSale(
                x.Sale.Id,
                x.Sale.DocDate,
                x.Sale.WarehouseCode,
                x.Sale.PaymentMethod,
                x.Sale.CardCode is { } code && !(walkInPartners.GetValueOrDefault(x.Sale.WarehouseCode)?.Contains(code) ?? false)
                    ? code
                    : null,
                x.Sale.TotalAmount,
                x.Sale.Quantity,
                Math.Round(x.Sale.TotalAmount / x.Average, 1, MidpointRounding.AwayFromZero)))
            .ToList();
    }

    // ── Arithmetic ─────────────────────────────────────────────────────────────────────────────────

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal PreciseAverage(decimal total, int count) => count == 0 ? 0 : total / count;

    private static decimal Percent(decimal part, decimal whole, int decimals) =>
        whole == 0 ? 0 : Math.Round(part / whole * 100m, decimals, MidpointRounding.AwayFromZero);

    /// <summary>The hour on the counter's clock for a UTC hour, through the named zone.</summary>
    private static int CatHour(int utcHour) =>
        AuditService.ToCAT(new DateTime(2026, 1, 1, utcHour, 0, 0, DateTimeKind.Utc)).Hour;

    private sealed record Cell(
        string Currency,
        string WarehouseCode,
        DateTime DocDate,
        string SourceSystem,
        string PaymentMethod,
        string CreatedBy,
        int SalesCount,
        decimal TotalAmount,
        decimal VatAmount,
        decimal ShortTendered);

    private sealed record HourCell(string Currency, string WarehouseCode, string SourceSystem, int Hour, int SalesCount, decimal TotalAmount);

    private sealed record LargeCandidate(
        int Id, string Currency, DateTime DocDate, string WarehouseCode, string PaymentMethod, string? CardCode, decimal TotalAmount, decimal Quantity);
}
