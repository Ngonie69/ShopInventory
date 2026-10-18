using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;

/// <summary>
/// Breaks a period's till takings down by how they were paid, and by when, where, who sold it, who it
/// was sold to, and what.
/// </summary>
/// <remarks>
/// A handful of grouped queries rather than one read of every sale. A month across every shop is tens of
/// thousands of rows, and none of the figures on this report needs a row — each is a sum over a group
/// small enough to hold. The payment-method normalisation happens after grouping, on the few distinct
/// values that come back, because legacy spellings cannot be folded in SQL without restating the rule.
/// <para>
/// Credits are the one thing read by the sale: <see cref="CreditedSales"/> reads the few sales of the
/// period that were credited back, and each is taken off the same groups it was added to. Nothing that
/// was sold is reduced — the credited figure stands beside it.
/// </para>
/// <para>
/// Margin, when asked for, is the management report's: SAP's booked gross profit on the invoices the
/// sales were posted as (<see cref="ManagementSalesRollup.MarginAsync"/>), so an item's GP here is its GP
/// there. The cost reader is optional so a caller that never asks for margin needs no SAP.
/// </para>
/// </remarks>
public sealed class GetDesktopSalesAnalysisHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ISaleInvoiceCostReader? costReader = null,
    ILogger<GetDesktopSalesAnalysisHandler>? logger = null)
    : IRequestHandler<GetDesktopSalesAnalysisQuery, ErrorOr<DesktopSalesAnalysisResult>>
{
    /// <summary>
    /// How long the cost read may hold the page. Past it the analysis is answered without margin. The
    /// reader is the stored copy, so this is a backstop; it was the SAP read's limit when the page asked SAP.
    /// </summary>
    public static readonly TimeSpan MarginDeadline = TimeSpan.FromSeconds(30);

    /// <summary>How many items the best-sellers list carries per currency for each line of business.</summary>
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
                ? $"Refused a sales analysis for warehouse {request.WarehouseCode}, partner {request.CardCode}."
                : $"Analysed {outcome.Value.Currencies.Sum(c => c.SalesCount)} sale(s) from "
                    + $"{outcome.Value.FromDate:yyyy-MM-dd} to {outcome.Value.ToDate:yyyy-MM-dd}"
                    + (outcome.Value.CardCode is { } partner ? $" for partner {partner}." : "."),
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
        var (previousFrom, previousTo) = PreviousPeriod(from, to);
        var source = string.IsNullOrWhiteSpace(request.SourceSystem) ? null : request.SourceSystem.Trim();
        var paymentMethod = string.IsNullOrWhiteSpace(request.PaymentMethod)
            ? null
            : TenderTypes.ReportingName(request.PaymentMethod);
        var cardCode = string.IsNullOrWhiteSpace(request.CardCode) ? null : request.CardCode.Trim();

        IQueryable<DesktopSaleEntity> Scope(DateTime windowFrom, DateTime windowTo)
        {
            var scoped = db.DesktopSales
                .AsNoTracking()
                .Where(s => s.DocDate >= windowFrom && s.DocDate <= windowTo);

            // The counters by default: tills and vending, and nothing off a van. The vans have their own
            // analysis (GetVanSalesAnalysisHandler), which reads the reservations too and so sees the online
            // sales this table holds only a receipt for. A van source is still read when it is asked for by
            // name, or as a business.
            if (source is not null)
            {
                scoped = scoped.Where(s => s.SourceSystem == source);
            }
            else if (!SaleBusinesses.IsKnown(request.Business))
            {
                scoped = scoped.AtCounters();
            }
            scoped = scoped.InBusiness(request.Business);

            // Inside the warehouse scope, never instead of it: the partner narrows what the caller may read.
            if (cardCode is not null)
            {
                scoped = scoped.Where(s => s.CardCode == cardCode);
            }

            return warehouse is null ? scoped : scoped.Where(s => s.WarehouseCode == warehouse);
        }

        (List<string> Spellings, bool IncludesAbsent)? tenderFilter = paymentMethod is null
            ? null
            : await StoredSpellingsAsync(Scope(previousFrom, to), paymentMethod, cancellationToken);

        IQueryable<DesktopSaleEntity> Filtered(DateTime windowFrom, DateTime windowTo)
        {
            var scoped = Scope(windowFrom, windowTo);
            if (tenderFilter is null)
            {
                return scoped;
            }

            var (spellings, includesAbsent) = tenderFilter.Value;
            return scoped.Where(s => spellings.Contains(s.PaymentMethod!) || (includesAbsent && s.PaymentMethod == null));
        }

        var sales = Filtered(from, to);

        // Every breakdown except the hour and the item is a roll-up of this one grouping, so it is read
        // once. Its rows number days × shops × partners × sources × operators × tenders at most, which
        // stays small: a counter sells as a handful of partners.
        var cells = (await sales
                .GroupBy(s => new { s.Currency, s.DocDate, s.WarehouseCode, s.SourceSystem, s.CreatedBy, s.PaymentMethod, s.CardCode })
                .Select(g => new
                {
                    g.Key.Currency,
                    g.Key.DocDate,
                    g.Key.WarehouseCode,
                    g.Key.SourceSystem,
                    g.Key.CreatedBy,
                    g.Key.PaymentMethod,
                    g.Key.CardCode,
                    // One name per cell is enough: a partner's sales all carry the same one, bar a card
                    // renamed part-way through the period, which the label settles by majority.
                    CardName = g.Max(s => s.CardName),
                    SalesCount = g.Count(),
                    TotalAmount = g.Sum(s => s.TotalAmount),
                    VatAmount = g.Sum(s => s.VatAmount),
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
                c.CardCode?.Trim() ?? string.Empty,
                string.IsNullOrWhiteSpace(c.CardName) ? null : c.CardName.Trim(),
                c.SalesCount,
                c.TotalAmount,
                c.VatAmount,
                c.WithoutReference))
            .ToList();

        // The credited sales of the period, each as a cell of its own beside the cell it was sold in:
        // no sale, no takings, only what was credited. Every roll-up below then nets itself, because a
        // credit lands in exactly the groups its sale did.
        var credited = await CreditedSales.InAsync(db, sales, cancellationToken);

        cells.AddRange(credited.Select(sale => new Cell(
            CurrencyKey(sale.Currency),
            sale.DocDate.Date,
            sale.WarehouseCode ?? string.Empty,
            sale.SourceSystem ?? string.Empty,
            sale.CreatedBy ?? string.Empty,
            TenderTypes.ReportingName(sale.PaymentMethod),
            sale.CardCode?.Trim() ?? string.Empty,
            null,
            0,
            0m,
            0m,
            0)
        {
            CreditedAmount = sale.CreditedAmount,
            CreditedSales = 1,
            FullyCredited = sale.IsFullyCredited ? 1 : 0
        }));

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

        hours.AddRange(credited.Select(sale => new HourCell(
            CurrencyKey(sale.Currency), CatHour(sale.CreatedAt.Hour), 0, 0m) { CreditedAmount = sale.CreditedAmount }));

        // By source as well as item, so an item is ranked inside the business that sold it: shops and
        // vending sell different ranges, and one list of both ranks a vendor's bulk line above everything a
        // counter sells. A sale has one source, so adding the sources of a business back together counts
        // no sale twice.
        var items = (await sales
                .SelectMany(s => s.Lines, (s, line) => new
                {
                    s.Currency,
                    s.SourceSystem,
                    line.SaleId,
                    line.ItemCode,
                    line.ItemDescription,
                    line.Quantity,
                    line.LineTotal,
                })
                .GroupBy(x => new { x.Currency, x.SourceSystem, x.ItemCode })
                .Select(g => new
                {
                    g.Key.Currency,
                    g.Key.SourceSystem,
                    g.Key.ItemCode,
                    ItemDescription = g.Max(x => x.ItemDescription),
                    Quantity = g.Sum(x => x.Quantity),
                    NetAmount = g.Sum(x => x.LineTotal),
                    SalesCount = g.Select(x => x.SaleId).Distinct().Count(),
                })
                .ToListAsync(cancellationToken))
            .GroupBy(i => (Currency: CurrencyKey(i.Currency), Business: SaleBusinesses.Of(i.SourceSystem), i.ItemCode))
            .Select(g => new ItemCell(
                g.Key.Currency,
                g.Key.Business,
                g.Key.ItemCode,
                g.Select(i => i.ItemDescription).Where(description => description is not null).Max(StringComparer.Ordinal),
                g.Sum(i => i.Quantity),
                g.Sum(i => i.NetAmount),
                g.Sum(i => i.SalesCount)))
            .ToList();

        // What was credited back of each item, inside the business that sold it. An item is only here
        // because it sold, so a credited line always finds its row.
        var creditedItems = credited
            .SelectMany(sale => sale.Lines, (sale, line) => (
                Currency: CurrencyKey(sale.Currency),
                Business: SaleBusinesses.Of(sale.SourceSystem),
                line.ItemCode,
                line.Quantity,
                line.Value))
            .GroupBy(line => (line.Currency, line.Business, line.ItemCode))
            .ToDictionary(g => g.Key, g => (Quantity: g.Sum(line => line.Quantity), Value: g.Sum(line => line.Value)));

        items = items
            .Select(item => creditedItems.TryGetValue((item.Currency, item.Business, item.ItemCode), out var back)
                ? item with { CreditedQuantity = back.Quantity, CreditedNetAmount = back.Value }
                : item)
            .ToList();

        // The same number of days just before, under the same filters, so a period can say whether it was
        // up or down on the one before without the page asking twice.
        var previous = (await Filtered(previousFrom, previousTo)
                .GroupBy(s => s.Currency)
                .Select(g => new { Currency = g.Key, SalesCount = g.Count(), TotalAmount = g.Sum(s => s.TotalAmount) })
                .ToListAsync(cancellationToken))
            .GroupBy(p => CurrencyKey(p.Currency))
            .ToDictionary(g => g.Key, g => (SalesCount: g.Sum(p => p.SalesCount), TotalAmount: g.Sum(p => p.TotalAmount)));

        ManagementSalesRollup.MarginBook? margin = null;
        ManagementMarginStatus? marginStatus = null;
        if (request.IncludeMargin)
        {
            (margin, marginStatus) = await MarginAsync(sales, cancellationToken);
        }

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
                operators,
                previous.GetValueOrDefault(group.Key),
                margin))
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
            currencies,
            paymentMethod,
            previousFrom,
            previousTo,
            cardCode)
        {
            CreditsCounted = true,
            Margin = marginStatus
        };
    }

    /// <summary>
    /// SAP's booked margin on the period's posted sales, or an unavailable status — never an error: the
    /// takings stand without it.
    /// </summary>
    private async Task<(ManagementSalesRollup.MarginBook Book, ManagementMarginStatus Status)> MarginAsync(
        IQueryable<DesktopSaleEntity> sales, CancellationToken cancellationToken)
    {
        if (costReader is null)
        {
            return (ManagementSalesRollup.MarginBook.Empty, new ManagementMarginStatus(
                false, "Gross profit is unavailable: this server has no SAP cost reader."));
        }

        return await ManagementSalesRollup.MarginAsync(
            sales,
            null,
            costReader,
            (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            cancellationToken,
            MarginDeadline);
    }

    private static DesktopSalesCurrencyAnalysis Analyse(
        string currency,
        List<Cell> cells,
        List<HourCell> hours,
        List<ItemCell> items,
        IReadOnlyList<string> paymentMethods,
        IReadOnlyDictionary<Guid, string> operators,
        (int SalesCount, decimal TotalAmount) previous,
        ManagementSalesRollup.MarginBook? margin)
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
                    Average(value, count),
                    Share(value, total),
                    Share(count, salesCount),
                    paidThisWay.Sum(c => c.WithoutReference))
                {
                    CreditedAmount = paidThisWay.Sum(c => c.CreditedAmount)
                };
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
                Split(day, paymentMethods))
            {
                CreditedAmount = day.Sum(c => c.CreditedAmount)
            })
            .ToList();

        var byHour = hours
            .GroupBy(h => h.Hour)
            .OrderBy(hour => hour.Key)
            .Select(hour => new DesktopSalesHourRow(hour.Key, hour.Sum(h => h.SalesCount), hour.Sum(h => h.TotalAmount))
            {
                CreditedAmount = hour.Sum(h => h.CreditedAmount)
            })
            .ToList();

        // One business at a time, in the order a review presents them. Nothing below ranks or shares an
        // item against another business's.
        var itemsByBusiness = SaleBusinesses.All
            .Select(business => (Business: business, Items: items.Where(i => i.Business == business).ToList()))
            .Where(group => group.Items.Count > 0)
            .ToList();

        var topItems = itemsByBusiness
            .SelectMany(group =>
            {
                var lineValue = group.Items.Sum(i => i.NetAmount);

                return group.Items
                    .OrderByDescending(i => i.NetAmount)
                    .ThenBy(i => i.ItemCode, StringComparer.OrdinalIgnoreCase)
                    .Take(TopItemCount)
                    .Select(i =>
                    {
                        var figure = ItemMargin(margin, currency, group.Business, i.ItemCode);
                        return new DesktopSalesItemRow(
                            i.ItemCode, i.ItemDescription, i.Quantity, i.NetAmount, i.SalesCount,
                            Share(i.NetAmount, lineValue), group.Business)
                        {
                            CreditedQuantity = i.CreditedQuantity,
                            CreditedNetAmount = i.CreditedNetAmount,
                            GrossProfit = figure?.GrossProfit,
                            CostedNetAmount = figure?.Revenue
                        };
                    });
            })
            .ToList();

        var partnerNames = PartnerNames(cells);

        return new DesktopSalesCurrencyAnalysis(
            currency,
            salesCount,
            total,
            vat,
            total - vat,
            Average(total, salesCount),
            items.Sum(i => i.Quantity),
            byDay.Count,
            items.Select(i => i.ItemCode).Distinct(StringComparer.Ordinal).Count(),
            byPaymentMethod,
            byDay,
            byHour,
            Breakdown(cells, c => c.WarehouseCode, code => string.IsNullOrEmpty(code) ? "Not recorded" : code, total, paymentMethods),
            Breakdown(cells, c => c.CardCode, code => PartnerLabel(code, partnerNames), total, paymentMethods),
            Breakdown(cells, c => c.SourceSystem, SourceLabel, total, paymentMethods),
            Breakdown(cells, c => c.CreatedBy, id => OperatorLabel(id, operators), total, paymentMethods),
            topItems,
            previous.SalesCount,
            previous.TotalAmount)
        {
            ItemsByBusiness = itemsByBusiness
                .Select(group =>
                {
                    var figure = ManagementSalesRollup.MarginFigure.Sum(
                        group.Items.Select(i => ItemMargin(margin, currency, group.Business, i.ItemCode)));
                    return new DesktopSalesBusinessItems(
                        group.Business,
                        SaleBusinesses.Label(group.Business),
                        group.Items.Count,
                        group.Items.Sum(i => i.Quantity),
                        group.Items.Sum(i => i.NetAmount))
                    {
                        CreditedQuantity = group.Items.Sum(i => i.CreditedQuantity),
                        CreditedNetAmount = group.Items.Sum(i => i.CreditedNetAmount),
                        GrossProfit = figure?.GrossProfit,
                        CostedNetAmount = figure?.Revenue
                    };
                })
                .ToList(),
            CreditedAmount = cells.Sum(c => c.CreditedAmount),
            CreditedSalesCount = cells.Sum(c => c.CreditedSales),
            FullyCreditedSalesCount = cells.Sum(c => c.FullyCredited),
            CreditedQuantity = items.Sum(i => i.CreditedQuantity)
        };
    }

    /// <summary>An item's booked margin inside the business that sold it, or null when none was costed.</summary>
    internal static ManagementSalesRollup.MarginFigure? ItemMargin(
        ManagementSalesRollup.MarginBook? margin, string currency, string business, string itemCode) =>
        margin?.For(currency, ManagementSalesRollup.Dimension.Item, ManagementSalesRollup.ItemKey(business, itemCode));

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
                Split(group, paymentMethods))
            {
                CreditedAmount = group.Sum(c => c.CreditedAmount)
            })
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

    /// <summary>
    /// Every stored spelling of a tender in the window, so the filter can be applied in SQL.
    /// </summary>
    /// <remarks>
    /// Tills have stored the same tender in several spellings, and <see cref="TenderTypes.ReportingName"/> is
    /// not something a query can restate. So the few distinct values the window holds are read first and folded
    /// here, and the query then filters on the raw values that fold to the one asked for. A sale with no tender
    /// at all is null or blank in the column, and matches only "Not recorded".
    /// </remarks>
    private static async Task<(List<string> Spellings, bool IncludesAbsent)> StoredSpellingsAsync(
        IQueryable<DesktopSaleEntity> window, string paymentMethod, CancellationToken cancellationToken)
    {
        var stored = await window
            .Select(s => s.PaymentMethod)
            .Where(method => method != null)
            .Distinct()
            .ToListAsync(cancellationToken);

        var spellings = stored
            .Where(method => TenderTypes.ReportingName(method) == paymentMethod)
            .Select(method => method!)
            .ToList();

        return (spellings, paymentMethod == TenderTypes.NotRecorded);
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

    /// <summary>
    /// What each business partner's sales called it: the name most of them carried, so a card renamed
    /// part-way through the period reads as one partner under one name rather than two.
    /// </summary>
    private static Dictionary<string, string> PartnerNames(IEnumerable<Cell> cells) =>
        cells
            .Where(c => c.CardCode.Length > 0 && c.CardName is not null)
            .GroupBy(c => c.CardCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                partner => partner.Key,
                partner => partner
                    .GroupBy(c => c.CardName!, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(name => name.Sum(c => c.SalesCount))
                    .ThenBy(name => name.Key, StringComparer.OrdinalIgnoreCase)
                    .First().Key,
                StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A business partner by its name, or by its code where none of its sales carried one — the code
    /// still says who, where a blank row would not.
    /// </summary>
    private static string PartnerLabel(string cardCode, IReadOnlyDictionary<string, string> names) =>
        cardCode.Length == 0 ? "Not recorded" : names.GetValueOrDefault(cardCode, cardCode);

    private static string SourceLabel(string source) => source switch
    {
        SaleSourceSystems.ShopTill => "Shop till",
        SaleSourceSystems.Vending => "Vending",
        SaleSourceSystems.VanSales => "Van sales",
        SaleSourceSystems.VanSalesOnline => "Van sales (online)",
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
    /// The same number of days immediately before the period.
    /// </summary>
    private static (DateTime From, DateTime To) PreviousPeriod(DateTime from, DateTime to)
    {
        var days = (to - from).Days + 1;
        return (from.AddDays(-days), from.AddDays(-1));
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
        string CardCode,
        string? CardName,
        int SalesCount,
        decimal TotalAmount,
        decimal VatAmount,
        int WithoutReference)
    {
        /// <summary>What was credited back, on a cell that stands for one credited sale and nothing else.</summary>
        public decimal CreditedAmount { get; init; }

        public int CreditedSales { get; init; }

        public int FullyCredited { get; init; }
    }

    private sealed record HourCell(string Currency, int Hour, int SalesCount, decimal TotalAmount)
    {
        public decimal CreditedAmount { get; init; }
    }

    private sealed record ItemCell(
        string Currency,
        string Business,
        string ItemCode,
        string? ItemDescription,
        decimal Quantity,
        decimal NetAmount,
        int SalesCount)
    {
        public decimal CreditedQuantity { get; init; }

        public decimal CreditedNetAmount { get; init; }
    }
}
