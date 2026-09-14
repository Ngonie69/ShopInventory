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
        CancellationToken cancellationToken)
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
            string.IsNullOrWhiteSpace(sourceSystem) ? null : sourceSystem.Trim());
    }

    /// <summary>
    /// The sales in a date range under the window's scope. An online van sale is left out unless its
    /// source is asked for by name, because its SAP invoice is already counted.
    /// </summary>
    public static IQueryable<DesktopSaleEntity> Sales(ApplicationDbContext db, Window window, DateTime start, DateTime end)
    {
        var sales = db.DesktopSales.AsNoTracking().Where(s => s.DocDate >= start && s.DocDate <= end);
        sales = window.SourceSystem is null
            ? sales.Where(s => s.SourceSystem != SaleSourceSystems.VanSalesOnline)
            : sales.Where(s => s.SourceSystem == window.SourceSystem);
        return window.WarehouseCode is null ? sales : sales.Where(s => s.WarehouseCode == window.WarehouseCode);
    }

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
    /// <param name="window">The period, whose end is extended by <see cref="PostingGraceDays"/> for the SAP read.</param>
    /// <param name="costReader">Where SAP's booked cost is read from.</param>
    /// <param name="logger">Where a failed SAP read is recorded.</param>
    /// <param name="cancellationToken">Cancels the database and SAP reads.</param>
    public static async Task<(MarginBook Book, ManagementMarginStatus Status)> MarginAsync(
        IQueryable<DesktopSaleEntity> sales,
        string? itemCode,
        Window window,
        ISaleInvoiceCostReader costReader,
        ILogger logger,
        CancellationToken cancellationToken)
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
                s.SourceSystem ?? string.Empty,
                s.CreatedBy ?? string.Empty,
                s.CostCentreCode?.Trim() ?? string.Empty,
                s.RouteCustomerId))
            .ToList();

        // One invoice for a day of a shop's sales: its lines are by item, so the margin can go on the item
        // and the shop, and nowhere finer.
        var consolidated = (await sales
                .Where(s => s.SapDocEntry == null && s.Consolidation != null && s.Consolidation.SapDocEntry != null)
                .GroupBy(s => new { DocEntry = s.Consolidation!.SapDocEntry!.Value, s.Currency, s.WarehouseCode })
                .Select(g => new { g.Key.DocEntry, g.Key.Currency, g.Key.WarehouseCode, SalesCount = g.Count() })
                .ToListAsync(cancellationToken))
            .Select(c => new ConsolidatedInvoice(c.DocEntry, CurrencyKey(c.Currency), c.WarehouseCode ?? string.Empty, c.SalesCount))
            .ToList();

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

            lines = await costReader.ReadAsync(warehouses, window.From, window.To.AddDays(PostingGraceDays), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Management sales report could not read invoice cost from SAP");
            return (MarginBook.Empty, new ManagementMarginStatus(
                false,
                "Gross margin is unavailable: SAP could not be reached for the booked cost of these sales. "
                    + "Everything else on this report is unaffected."));
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
            detail += " Sales posted as a day's consolidated invoice carry margin by item and shop only.";
        }

        return (MarginBook.From(direct, consolidated, lines), new ManagementMarginStatus(true, detail));
    }

    /// <summary>
    /// How long after a period ends its sales may still be posted and dated. SAP invoices are read this
    /// far past the end so a sale posted a few days late still carries its cost into the period it was
    /// made in; matching is by document number, so nothing from outside the period is counted.
    /// </summary>
    public const int PostingGraceDays = 14;

    // ── Shapes ─────────────────────────────────────────────────────────────────────────────────────

    public sealed record Window(
        DateTime From,
        DateTime To,
        DateTime PreviousFrom,
        DateTime PreviousTo,
        int Days,
        string? WarehouseCode,
        string? SourceSystem);

    public sealed record Labels(
        IReadOnlyDictionary<Guid, string> Operators,
        Dictionary<string, (string Code, string Name)> Shops,
        Dictionary<int, (string Code, string Name, string BusinessPartnerCode)> Vendors);

    public sealed record PostedSale(
        int DocEntry, string Currency, string WarehouseCode, string SourceSystem, string CreatedBy, string CostCentreCode, int? RouteCustomerId);

    public sealed record ConsolidatedInvoice(int DocEntry, string Currency, string WarehouseCode, int SalesCount);

    public enum Dimension { Total, Channel, Depot, CostCentre, Operator, Vendor, Item }

    public sealed record MarginFigure(decimal Revenue, decimal GrossProfit)
    {
        public decimal? MarginPercent => Revenue == 0 ? null : Math.Round(GrossProfit / Revenue * 100m, 1, MidpointRounding.AwayFromZero);

        public static MarginFigure? Sum(IEnumerable<MarginFigure?> figures)
        {
            var known = figures.Where(figure => figure is not null).Select(figure => figure!).ToList();
            return known.Count == 0 ? null : new MarginFigure(known.Sum(f => f.Revenue), known.Sum(f => f.GrossProfit));
        }
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
                foreach (var line in docLines)
                {
                    book.Add(sale.Currency, Dimension.Total, string.Empty, line);
                    book.Add(sale.Currency, Dimension.Item, line.ItemCode, line);
                    book.Add(sale.Currency, Dimension.Depot, sale.WarehouseCode, line);
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
                foreach (var line in docLines)
                {
                    book.Add(invoice.Currency, Dimension.Total, string.Empty, line);
                    book.Add(invoice.Currency, Dimension.Item, line.ItemCode, line);
                    book.Add(invoice.Currency, Dimension.Depot, invoice.WarehouseCode, line);
                }
            }

            return book;
        }

        public MarginFigure? Total(string currency) => For(currency, Dimension.Total, string.Empty);

        public MarginFigure? For(string currency, Dimension dimension, string key) =>
            _figures.TryGetValue((currency, dimension, key.ToUpperInvariant()), out var figure)
                ? new MarginFigure(figure.Revenue, figure.GrossProfit)
                : null;

        public int CostedSales(string currency) => _costedSales.GetValueOrDefault(currency);

        private void CountCosted(string currency, int sales) =>
            _costedSales[currency] = _costedSales.GetValueOrDefault(currency) + sales;

        private void Add(string currency, Dimension dimension, string key, SaleInvoiceLineCost line)
        {
            var slot = (currency, dimension, key.ToUpperInvariant());
            var (revenue, profit) = _figures.GetValueOrDefault(slot);
            _figures[slot] = (revenue + line.Revenue, profit + line.GrossProfit);
        }
    }
}
