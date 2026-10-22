using System.Globalization;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;

/// <summary>
/// What the management sales report and its item drill-down share: which sales are read, how a period is
/// compared, how rows are named, and where margin comes from.
/// </summary>
/// <remarks>
/// One place, so the report's figure for an item and the drill-down's figure for the same item are the
/// same number by construction rather than by two handlers agreeing.
/// </remarks>
internal static class ManagementSalesRollup
{
    public const string NotRecorded = "Not recorded";

    /// <summary>The scope and the two periods a request reads, or why it may not.</summary>
    public static async Task<ErrorOr<Window>> WindowAsync(
        ApplicationDbContext db,
        Guid callerUserId,
        DateTime? fromDate,
        DateTime? toDate,
        string? warehouseCode,
        string? sourceSystem,
        string? cardCode,
        CancellationToken cancellationToken,
        string? business = null)
    {
        var caller = await db.Users
            .AsNoTracking()
            .Include(user => user.Shop)
            .FirstOrDefaultAsync(user => user.Id == callerUserId, cancellationToken);

        var scope = DesktopSalesReadScopeResolver.Resolve(caller);
        if (scope.IsError)
        {
            return scope.Errors;
        }

        var readScope = scope.Value.Narrow(warehouseCode);
        if (readScope.IsError)
        {
            return readScope.Errors;
        }

        var today = AuditService.ToCAT(DateTime.UtcNow).Date;
        var from = (fromDate ?? toDate ?? today).Date;
        var to = (toDate ?? (from > today ? from : today)).Date;
        var days = (to - from).Days + 1;

        return new Window(
            from,
            to,
            from.AddDays(-days),
            from.AddDays(-1),
            days,
            readScope.Value.WarehouseCode,
            string.IsNullOrWhiteSpace(sourceSystem) ? null : sourceSystem.Trim(),
            string.IsNullOrWhiteSpace(cardCode) ? null : cardCode.Trim(),
            SaleBusinesses.IsKnown(business) ? business!.Trim().ToLowerInvariant() : null);
    }

    /// <summary>
    /// The sales in a date range under the window's scope: the counters', shop tills and vending. A van
    /// sale, uploaded offline or receipted online, is left out unless its source or its business is asked
    /// for by name — the vans are reported under van sales, from their own reader.
    /// </summary>
    public static IQueryable<DesktopSaleEntity> Sales(ApplicationDbContext db, Window window, DateTime start, DateTime end)
    {
        var sales = db.DesktopSales.AsNoTracking().Where(s => s.DocDate >= start && s.DocDate <= end);
        if (window.SourceSystem is not null)
        {
            sales = sales.Where(s => s.SourceSystem == window.SourceSystem);
        }
        else if (window.Business is null)
        {
            sales = sales.AtCounters();
        }

        sales = sales.InBusiness(window.Business);
        if (window.WarehouseCode is not null)
        {
            sales = sales.Where(s => s.WarehouseCode == window.WarehouseCode);
        }

        return window.CardCode is null ? sales : sales.Where(s => s.CardCode == window.CardCode);
    }

    /// <summary>
    /// Every business partner that sold under the caller's scope in either period, whatever the channel
    /// and partner filters say — the list the page chooses a partner from, so choosing one never empties it.
    /// </summary>
    /// <remarks>
    /// A partner, not a warehouse, is who a sale was made as: one warehouse can serve several partners, so
    /// a warehouse filter mixes them together.
    /// </remarks>
    public static async Task<List<ManagementPartner>> PartnersAsync(
        ApplicationDbContext db, Window window, CancellationToken cancellationToken) =>
        (await Sales(db, window with { SourceSystem = null, CardCode = null }, window.PreviousFrom, window.To)
                .GroupBy(s => new { s.CardCode, s.WarehouseCode })
                .Select(g => new { g.Key.CardCode, g.Key.WarehouseCode, CardName = g.Max(s => s.CardName) })
                .ToListAsync(cancellationToken))
            .Where(p => !string.IsNullOrWhiteSpace(p.CardCode))
            .GroupBy(p => p.CardCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new ManagementPartner(
                g.Key,
                g.Select(p => p.CardName?.Trim()).FirstOrDefault(name => !string.IsNullOrEmpty(name)) ?? g.Key,
                g.Select(p => p.WarehouseCode?.Trim() ?? string.Empty)
                    .Where(code => code.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .OrderBy(p => p.CardName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.CardCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ── Names ──────────────────────────────────────────────────────────────────────────────────────

    public static async Task<Labels> LabelsAsync(
        ApplicationDbContext db,
        IEnumerable<string> createdBy,
        IEnumerable<int> vendorIds,
        CancellationToken cancellationToken)
    {
        var operators = await SaleOperatorNames.ResolveAsync(db, createdBy, cancellationToken);

        // Active shop first, then the oldest, as the invoice remarks resolve a shop from its warehouse.
        var shops = (await db.Shops.AsNoTracking()
                .Select(shop => new { shop.Id, shop.Code, shop.Name, shop.WarehouseCode, shop.IsActive })
                .ToListAsync(cancellationToken))
            .Where(shop => !string.IsNullOrWhiteSpace(shop.WarehouseCode))
            .GroupBy(shop => shop.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(shop => shop.IsActive).ThenBy(shop => shop.Id)
                    .Select(shop => (shop.Code, shop.Name)).First(),
                StringComparer.OrdinalIgnoreCase);

        var ids = vendorIds.Distinct().ToList();
        var vendors = ids.Count == 0
            ? new Dictionary<int, (string Code, string Name, string BusinessPartnerCode)>()
            : (await db.RouteCustomers.AsNoTracking()
                    .Where(customer => ids.Contains(customer.Id))
                    .Select(customer => new { customer.Id, customer.Code, customer.Name, customer.Surname, customer.AssignedBusinessPartnerCode })
                    .ToListAsync(cancellationToken))
                .ToDictionary(
                    customer => customer.Id,
                    customer => (
                        customer.Code,
                        string.IsNullOrWhiteSpace(customer.Surname) ? customer.Name : $"{customer.Name} {customer.Surname}",
                        customer.AssignedBusinessPartnerCode));

        return new Labels(operators, shops, vendors);
    }

    /// <summary>
    /// A shop by its name; a vending depot or a van, which has no shop row, by its warehouse with the
    /// partner it sells under as the hint — the page swaps in the partner's name where it knows it.
    /// </summary>
    public static (string Label, string? Hint) DepotLabel(string warehouse, Labels labels, IEnumerable<string> partners)
    {
        if (warehouse.Length == 0)
        {
            return (NotRecorded, null);
        }

        if (labels.Shops.TryGetValue(warehouse, out var shop))
        {
            return (shop.Name, $"{shop.Code} · {warehouse}");
        }

        var codes = partners.Where(code => code.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return (warehouse, codes.Count == 0 ? null : string.Join(", ", codes));
    }

    /// <summary>
    /// A business partner by its name, with its code and the warehouses its sales left from as the hint.
    /// </summary>
    public static (string Label, string? Hint) PartnerLabel(
        string cardCode, IReadOnlyDictionary<string, ManagementPartner> partners, IEnumerable<string> warehouses)
    {
        if (cardCode.Length == 0)
        {
            return (NotRecorded, null);
        }

        var codes = warehouses.Where(code => code.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase).ToList();
        var hint = codes.Count == 0 ? cardCode : $"{cardCode} · {string.Join(", ", codes)}";
        return (partners.TryGetValue(cardCode, out var partner) ? partner.CardName : cardCode, hint);
    }

    public static Dictionary<string, ManagementPartner> PartnerDirectory(IEnumerable<ManagementPartner> partners) =>
        partners.ToDictionary(p => p.CardCode, StringComparer.OrdinalIgnoreCase);

    public static (string Label, string? Hint) VendorLabel(string key, Labels labels) =>
        int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
        && labels.Vendors.TryGetValue(id, out var vendor)
            ? (vendor.Name, $"{vendor.Code} · {vendor.BusinessPartnerCode}")
            : ($"Vendor {key}", null);

    public static string OperatorLabel(string createdBy, Labels labels) =>
        string.IsNullOrWhiteSpace(createdBy) ? NotRecorded : SaleOperatorNames.Label(createdBy, labels.Operators) ?? createdBy;

    public static string SourceLabel(string source) => source switch
    {
        SaleSourceSystems.ShopTill => "Shop till",
        SaleSourceSystems.Vending => "Vending",
        SaleSourceSystems.VanSales => "Van sales",
        SaleSourceSystems.VanSalesOnline => "Van sales (online receipts)",
        SaleSourceSystems.LegacyDesktop => "Desktop (legacy)",
        "" => NotRecorded,
        _ => source
    };

    public static string VendorKey(int id) => id.ToString(CultureInfo.InvariantCulture);

    // ── Arithmetic ─────────────────────────────────────────────────────────────────────────────────

    public static string CurrencyKey(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? NotRecorded : currency.Trim().ToUpperInvariant();

    /// <summary>Null when there was nothing before, rather than an infinite rise.</summary>
    public static decimal? Change(decimal now, decimal before) =>
        before == 0 ? null : Math.Round((now - before) / Math.Abs(before) * 100m, 1, MidpointRounding.AwayFromZero);

    public static decimal Share(decimal part, decimal whole) =>
        whole == 0 ? 0 : Math.Round(part / whole * 100m, 1, MidpointRounding.AwayFromZero);

    public static decimal Average(decimal total, int count) =>
        count == 0 ? 0 : Math.Round(total / count, 2, MidpointRounding.AwayFromZero);

    public static decimal PerUnit(decimal value, decimal quantity) =>
        quantity == 0 ? 0 : Math.Round(value / quantity, 4, MidpointRounding.AwayFromZero);

    /// <summary>Currencies in reading order: dollars first, as the tills price in them, then by name.</summary>
    public static IEnumerable<T> InCurrencyOrder<T>(IEnumerable<T> sections, Func<T, string> currency) =>
        sections.OrderBy(section => currency(section) == "USD" ? 0 : 1).ThenBy(currency, StringComparer.Ordinal);

    // ── Margin ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The margin the posted sales in <paramref name="sales"/> carry, from the invoices SAP holds for them.
    /// </summary>
    /// <param name="sales">The period's sales, already scoped.</param>
    /// <param name="itemCode">When given, only that item's invoice lines count — the drill-down's margin.</param>
    /// <param name="costReader">Where SAP's booked cost is read from.</param>
    /// <param name="logger">Where a failed SAP read is recorded.</param>
    /// <param name="cancellationToken">Cancels the database and SAP reads.</param>
    /// <param name="deadline">How long the SAP read may take; see <see cref="MarginOfPostedAsync"/>.</param>
    public static async Task<(MarginBook Book, ManagementMarginStatus Status)> MarginAsync(
        IQueryable<DesktopSaleEntity> sales,
        string? itemCode,
        ISaleInvoiceCostReader costReader,
        ILogger logger,
        CancellationToken cancellationToken,
        TimeSpan? deadline = null)
    {
        if (itemCode is not null)
        {
            sales = sales.Where(s => s.Lines.Any(line => line.ItemCode == itemCode));
        }

        // One invoice per sale: every dimension of the sale is known, so the margin can go on any row.
        var direct = (await sales
                .Where(s => s.SapDocEntry != null)
                .Select(s => new
                {
                    DocEntry = s.SapDocEntry!.Value,
                    s.Currency,
                    s.WarehouseCode,
                    s.CardCode,
                    s.SourceSystem,
                    s.CreatedBy,
                    s.CostCentreCode,
                    s.RouteCustomerId,
                })
                .ToListAsync(cancellationToken))
            .Select(s => new PostedSale(
                s.DocEntry,
                CurrencyKey(s.Currency),
                s.WarehouseCode ?? string.Empty,
                s.CardCode?.Trim() ?? string.Empty,
                s.SourceSystem ?? string.Empty,
                s.CreatedBy ?? string.Empty,
                s.CostCentreCode?.Trim() ?? string.Empty,
                s.RouteCustomerId))
            .ToList();

        // One invoice for a day of a shop's sales to one partner (consolidation groups by CardCode): its
        // lines are by item, so the margin can go on the item, the shop and the partner, and nowhere finer.
        var consolidated = (await sales
                .Where(s => s.SapDocEntry == null && s.Consolidation != null && s.Consolidation.SapDocEntry != null)
                .GroupBy(s => new { DocEntry = s.Consolidation!.SapDocEntry!.Value, s.Currency, s.WarehouseCode, s.CardCode })
                .Select(g => new { g.Key.DocEntry, g.Key.Currency, g.Key.WarehouseCode, g.Key.CardCode, SalesCount = g.Count() })
                .ToListAsync(cancellationToken))
            .Select(c => new ConsolidatedInvoice(
                c.DocEntry, CurrencyKey(c.Currency), c.WarehouseCode ?? string.Empty, c.CardCode ?? string.Empty, c.SalesCount))
            .ToList();

        return await MarginOfPostedAsync(direct, consolidated, itemCode, costReader, logger, deadline, cancellationToken);
    }

    /// <summary>
    /// The margin SAP booked on invoices already known by number: what <see cref="MarginAsync"/> does once
    /// it has found them, for a report whose sales are not all desktop sales — the vans' online invoices
    /// are known from their reservations.
    /// </summary>
    /// <param name="deadline">
    /// How long the SAP read may take before margin is reported unavailable rather than holding the
    /// report; null waits as long as the caller does.
    /// </param>
    public static async Task<(MarginBook Book, ManagementMarginStatus Status)> MarginOfPostedAsync(
        List<PostedSale> direct,
        List<ConsolidatedInvoice> consolidated,
        string? itemCode,
        ISaleInvoiceCostReader costReader,
        ILogger logger,
        TimeSpan? deadline,
        CancellationToken cancellationToken)
    {
        if (direct.Count == 0 && consolidated.Count == 0)
        {
            return (MarginBook.Empty, new ManagementMarginStatus(
                true,
                "No sale in this period has reached SAP yet, so there is no booked cost to measure margin against."));
        }

        IReadOnlyList<SaleInvoiceLineCost> lines;
        try
        {
            var warehouses = direct.Select(s => s.WarehouseCode)
                .Concat(consolidated.Select(c => c.WarehouseCode))
                .Where(code => code.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (deadline is { } limit)
            {
                timeout.CancelAfter(limit);
            }

            var docEntries = direct.Select(s => s.DocEntry)
                .Concat(consolidated.Select(c => c.DocEntry))
                .Distinct()
                .ToList();

            lines = await costReader.ReadAsync(warehouses, docEntries, timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The deadline is the usual way here, and "could not be reached" sent the last diagnosis
            // looking for an outage when SAP was answering everything else in 40 ms.
            string reason;
            if (ex is OperationCanceledException && deadline is { } missed)
            {
                logger.LogWarning(ex, "Invoice cost from SAP did not arrive within the {Deadline} margin deadline", missed);
                reason = $"SAP did not return the booked cost of these sales within {missed.TotalSeconds:0} seconds.";
            }
            else
            {
                logger.LogWarning(ex, "Could not read invoice cost from SAP for a sales margin");
                reason = "SAP could not be reached for the booked cost of these sales.";
            }

            return (MarginBook.Empty, new ManagementMarginStatus(
                false,
                $"Gross margin is unavailable: {reason} Everything else on this report is unaffected."));
        }

        if (itemCode is not null)
        {
            lines = lines.Where(line => string.Equals(line.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var detail = "Gross margin is SAP's booked gross profit on the invoices these sales were posted as, "
            + "at the stock cost when the goods left. It is measured only on sales SAP holds; the rest are "
            + "counted under posting.";
        if (consolidated.Count > 0)
        {
            detail += " Sales posted as a day's consolidated invoice carry margin by item, shop and business partner only.";
        }

        return (MarginBook.From(direct, consolidated, lines), new ManagementMarginStatus(true, detail));
    }

    // ── Shapes ─────────────────────────────────────────────────────────────────────────────────────

    public sealed record Window(
        DateTime From,
        DateTime To,
        DateTime PreviousFrom,
        DateTime PreviousTo,
        int Days,
        string? WarehouseCode,
        string? SourceSystem,
        string? CardCode = null,
        string? Business = null);

    public sealed record Labels(
        IReadOnlyDictionary<Guid, string> Operators,
        Dictionary<string, (string Code, string Name)> Shops,
        Dictionary<int, (string Code, string Name, string BusinessPartnerCode)> Vendors);

    public sealed record PostedSale(
        int DocEntry, string Currency, string WarehouseCode, string CardCode, string SourceSystem, string CreatedBy, string CostCentreCode, int? RouteCustomerId);

    public sealed record ConsolidatedInvoice(int DocEntry, string Currency, string WarehouseCode, string CardCode, int SalesCount);

    public enum Dimension { Total, Channel, Depot, Partner, CostCentre, Operator, Vendor, Item }

    /// <summary>
    /// What an item's margin is filed under: the item inside the line of business that sold it. Shops and
    /// vending are costed apart, like everything else about their items.
    /// </summary>
    public static string ItemKey(string business, string itemCode) => $"{business}|{itemCode}";

    public sealed record MarginFigure(decimal Revenue, decimal GrossProfit)
    {
        public decimal? MarginPercent => Revenue == 0 ? null : Math.Round(GrossProfit / Revenue * 100m, 1, MidpointRounding.AwayFromZero);

        public static MarginFigure? Sum(IEnumerable<MarginFigure?> figures)
        {
            var known = figures.Where(figure => figure is not null).Select(figure => figure!).ToList();
            return known.Count == 0 ? null : new MarginFigure(known.Sum(f => f.Revenue), known.Sum(f => f.GrossProfit));
        }
    }

    /// <summary>SAP's booked revenue and gross profit on some sales, and how many of those sales it covers.</summary>
    /// <param name="Revenue">The costed sales' value before VAT, as SAP invoiced it.</param>
    /// <param name="GrossProfit">SAP's gross profit on them.</param>
    /// <param name="CostedSales">How many of the sales asked about were costed.</param>
    public sealed record SalesMargin(decimal Revenue, decimal GrossProfit, int CostedSales)
    {
        public decimal? MarginPercent => Revenue == 0 ? null : Math.Round(GrossProfit / Revenue * 100m, 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>SAP's revenue and gross profit, summed onto every row it can honestly be put on.</summary>
    public sealed class MarginBook
    {
        public static readonly MarginBook Empty = new();

        private readonly Dictionary<(string Currency, Dimension Dimension, string Key), (decimal Revenue, decimal GrossProfit)> _figures = new();
        private readonly Dictionary<string, int> _costedSales = new(StringComparer.Ordinal);

        public static MarginBook From(
            List<PostedSale> direct, List<ConsolidatedInvoice> consolidated, IReadOnlyList<SaleInvoiceLineCost> lines)
        {
            var book = new MarginBook();
            var linesByDoc = lines.GroupBy(line => line.DocEntry).ToDictionary(g => g.Key, g => g.ToList());

            // A sale posted twice would be a posting fault, but counting its invoice once is still right.
            foreach (var sale in direct.GroupBy(s => s.DocEntry).Select(g => g.First()))
            {
                if (!linesByDoc.TryGetValue(sale.DocEntry, out var docLines))
                {
                    continue;
                }

                book.CountCosted(sale.Currency, 1);
                book.BeginSale(1);
                foreach (var line in docLines)
                {
                    book.AddInvoice(sale.DocEntry, line);
                    book.Add(sale.Currency, Dimension.Total, string.Empty, line);
                    book.Add(sale.Currency, Dimension.Item, ItemKey(SaleBusinesses.Of(sale.SourceSystem), line.ItemCode), line);
                    book.Add(sale.Currency, Dimension.Depot, sale.WarehouseCode, line);
                    book.Add(sale.Currency, Dimension.Partner, sale.CardCode, line);
                    book.Add(sale.Currency, Dimension.Channel, sale.SourceSystem, line);
                    book.Add(sale.Currency, Dimension.CostCentre, sale.CostCentreCode, line);
                    book.Add(sale.Currency, Dimension.Operator, sale.CreatedBy, line);
                    if (sale.RouteCustomerId is { } vendor)
                    {
                        book.Add(sale.Currency, Dimension.Vendor, VendorKey(vendor), line);
                    }
                }
            }

            foreach (var invoice in consolidated)
            {
                if (!linesByDoc.TryGetValue(invoice.DocEntry, out var docLines))
                {
                    continue;
                }

                book.CountCosted(invoice.Currency, invoice.SalesCount);
                book.BeginSale(invoice.SalesCount);
                foreach (var line in docLines)
                {
                    book.Add(invoice.Currency, Dimension.Total, string.Empty, line);

                    // Only the legacy desktop route consolidates — vending and the vans post one invoice a
                    // sale — so a consolidated invoice's items are the shops'.
                    book.Add(invoice.Currency, Dimension.Item, ItemKey(SaleBusinesses.Shops, line.ItemCode), line);
                    book.Add(invoice.Currency, Dimension.Depot, invoice.WarehouseCode, line);
                    book.Add(invoice.Currency, Dimension.Partner, invoice.CardCode, line);
                }
            }

            return book;
        }

        public MarginFigure? Total(string currency) => For(currency, Dimension.Total, string.Empty);

        public MarginFigure? For(string currency, Dimension dimension, string key) =>
            _figures.TryGetValue((currency, dimension, key.ToUpperInvariant()), out var figure)
                ? new MarginFigure(figure.Revenue, figure.GrossProfit)
                : null;

        /// <summary>
        /// <see cref="For"/> with how many sales it was measured on, so a row can say when only some of its
        /// sales are in SAP yet.
        /// </summary>
        public SalesMargin? MarginOf(string currency, Dimension dimension, string key)
        {
            var slot = (currency, dimension, key.ToUpperInvariant());
            return _figures.TryGetValue(slot, out var figure)
                ? new SalesMargin(figure.Revenue, figure.GrossProfit, _slotSales.GetValueOrDefault(slot))
                : null;
        }

        public int CostedSales(string currency) => _costedSales.GetValueOrDefault(currency);

        /// <summary>
        /// The margin on a set of sales, each posted as its own invoice: what a report sums onto rows the
        /// dimensions above do not cover, such as a day, an hour or a route.
        /// </summary>
        /// <remarks>
        /// A sale on a day's consolidated invoice has no invoice of its own, so it is never costed here.
        /// Each invoice counts once however often it is named. Null when none of them is costed.
        /// </remarks>
        public SalesMargin? ForInvoices(IEnumerable<int?> docEntries)
        {
            decimal revenue = 0, profit = 0;
            var costed = 0;
            foreach (var docEntry in docEntries.OfType<int>().Distinct())
            {
                if (_invoices.TryGetValue(docEntry, out var figure))
                {
                    revenue += figure.Revenue;
                    profit += figure.GrossProfit;
                    costed++;
                }
            }

            return costed == 0 ? null : new SalesMargin(revenue, profit, costed);
        }

        /// <summary>One item's margin on a set of sales each posted as its own invoice; see <see cref="ForInvoices"/>.</summary>
        public SalesMargin? ForInvoiceItem(IEnumerable<int?> docEntries, string itemCode)
        {
            decimal revenue = 0, profit = 0;
            var costed = 0;
            var key = itemCode.Trim().ToUpperInvariant();
            foreach (var docEntry in docEntries.OfType<int>().Distinct())
            {
                if (_invoiceItems.TryGetValue((docEntry, key), out var figure))
                {
                    revenue += figure.Revenue;
                    profit += figure.GrossProfit;
                    costed++;
                }
            }

            return costed == 0 ? null : new SalesMargin(revenue, profit, costed);
        }

        private readonly Dictionary<int, (decimal Revenue, decimal GrossProfit)> _invoices = new();
        private readonly Dictionary<(int DocEntry, string ItemCode), (decimal Revenue, decimal GrossProfit)> _invoiceItems = new();

        private void AddInvoice(int docEntry, SaleInvoiceLineCost line)
        {
            var (revenue, profit) = _invoices.GetValueOrDefault(docEntry);
            _invoices[docEntry] = (revenue + line.Revenue, profit + line.GrossProfit);

            var itemSlot = (docEntry, line.ItemCode.Trim().ToUpperInvariant());
            var (itemRevenue, itemProfit) = _invoiceItems.GetValueOrDefault(itemSlot);
            _invoiceItems[itemSlot] = (itemRevenue + line.Revenue, itemProfit + line.GrossProfit);
        }

        private void CountCosted(string currency, int sales) =>
            _costedSales[currency] = _costedSales.GetValueOrDefault(currency) + sales;

        // How many sales each slot was measured on: an invoice's sales count once per slot, however many
        // of its lines land there.
        private readonly Dictionary<(string Currency, Dimension Dimension, string Key), int> _slotSales = new();
        private readonly HashSet<(string Currency, Dimension Dimension, string Key)> _saleSlots = new();
        private int _saleWeight;

        private void BeginSale(int sales)
        {
            _saleSlots.Clear();
            _saleWeight = sales;
        }

        private void Add(string currency, Dimension dimension, string key, SaleInvoiceLineCost line)
        {
            var slot = (currency, dimension, key.ToUpperInvariant());
            var (revenue, profit) = _figures.GetValueOrDefault(slot);
            _figures[slot] = (revenue + line.Revenue, profit + line.GrossProfit);
            if (_saleSlots.Add(slot))
            {
                _slotSales[slot] = _slotSales.GetValueOrDefault(slot) + _saleWeight;
            }
        }
    }
}
