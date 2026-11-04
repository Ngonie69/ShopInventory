using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppSettings;
using ShopInventory.Features.VanSalesReports.Queries.GetVanDailyTruckReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppReport;

/// <summary>
/// Lays one day's sales out as the WhatsApp report: the vans from the daily truck report, the shop from
/// its till sales.
/// </summary>
/// <remarks>
/// <para>
/// The vans are the daily truck report's own figures — dollars, VAT included, both reps on a truck
/// together — so the message cannot disagree with that page. A van row adds up the routes it names; a
/// route no row names is still counted, on an "Other routes" line, and van sales by a login on no route
/// on a "No route" line, so the total is always the whole fleet.
/// </para>
/// <para>
/// The shop is read straight from its till sales rather than through the sales analysis, whose item
/// list stops at its best sellers: a product on the list that sold little must still show its number.
/// It is scoped as the analysis scopes shops — the warehouse, the day, and the shop sources only.
/// </para>
/// <para>
/// The counter section is every shop and every vending business partner under the same scope rules, each
/// split by the tender the till recorded. Every active shop is listed, a day without sales included, so a shop that did not trade
/// — or whose till did not sync — shows as such rather than going missing.
/// </para>
/// </remarks>
public sealed class GetDailySalesWhatsAppReportHandler(
    ApplicationDbContext db,
    IMediator mediator,
    TimeProvider? clock = null)
    : IRequestHandler<GetDailySalesWhatsAppReportQuery, ErrorOr<DailySalesWhatsAppReport>>
{
    public const string OtherRoutesLabel = "Other routes";
    public const string NoRouteLabel = "No route";

    public async Task<ErrorOr<DailySalesWhatsAppReport>> Handle(
        GetDailySalesWhatsAppReportQuery request, CancellationToken cancellationToken)
    {
        var day = request.TradingDate.Date;

        var settings = await mediator.Send(new GetDailySalesWhatsAppSettingsQuery(), cancellationToken);
        if (settings.IsError)
        {
            return settings.Errors;
        }

        var trucks = await mediator.Send(new GetVanDailyTruckReportQuery(day, day), cancellationToken);
        if (trucks.IsError)
        {
            return trucks.Errors;
        }

        var days = trucks.Value.Days.Where(row => row.TradingDate.Date == day).ToList();
        var vanLines = VanLines(days, settings.Value.VanRows, trucks.Value.SalesOutsideRoutes, trucks.Value.SalesOutsideRoutesByTender);
        var vanOther = days
            .Where(row => row.OtherCurrencySales is { } amount && amount != 0m)
            .GroupBy(row => row.OtherCurrencyCodes ?? "Other")
            .Select(group => new DailySalesWhatsAppCurrencyAmount(group.Key, group.Sum(row => row.OtherCurrencySales!.Value)))
            .OrderBy(amount => amount.Currency, StringComparer.Ordinal)
            .ToList();

        var shop = settings.Value.ShopWarehouseCode is { } warehouse
            ? await ShopAsync(warehouse, Heading(settings.Value), settings.Value.ProductRows, day, cancellationToken)
            : null;

        var counters = await CountersAsync(day, cancellationToken);

        var vanTotal = vanLines.Sum(line => line.Sales);
        var vanTenders = VanTotalTenders(vanLines);
        var message = DailySalesWhatsAppMessage.Build(day, vanLines, vanTotal, vanTenders, vanOther, shop, counters);

        return new DailySalesWhatsAppReport(
            day, vanLines, vanTotal, vanTenders, vanOther, shop, counters, message, (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);
    }

    /// <summary>The van table's lines for one day's rows of the daily truck report.</summary>
    internal static List<DailySalesWhatsAppVanLine> VanLines(
        IReadOnlyList<VanDailyTruckDayDto> days,
        IReadOnlyList<DailySalesWhatsAppRow> rows,
        decimal salesOutsideRoutes,
        VanDailyTruckTenders? outsideRoutesByTender = null)
    {
        var lines = new List<DailySalesWhatsAppVanLine>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (rows.Count == 0)
        {
            // Nothing set up: every route under its own name, in the truck report's order. Upcountry trips
            // move town to town, so their area is part of the name — "Upc 1 - Zaka" — as in the office's sheet.
            foreach (var route in days)
            {
                var label = IsUpcountry(route.Territory) ? WithArea(route.RouteName, [route]) : route.RouteName;
                lines.Add(new DailySalesWhatsAppVanLine(label, route.Sales, [route.RouteCode], VanTenders([route])));
                claimed.Add(route.RouteCode);
            }
        }
        else
        {
            foreach (var row in rows)
            {
                var matched = days.Where(route => row.Codes.Contains(route.RouteCode, StringComparer.OrdinalIgnoreCase)).ToList();
                foreach (var route in matched)
                {
                    claimed.Add(route.RouteCode);
                }

                var label = row.ShowArea ? WithArea(row.Label, matched) : row.Label;
                lines.Add(new DailySalesWhatsAppVanLine(label, matched.Sum(route => route.Sales), row.Codes, VanTenders(matched)));
            }
        }

        var unclaimed = days.Where(route => !claimed.Contains(route.RouteCode) && route.Sales != 0m).ToList();
        if (unclaimed.Count > 0)
        {
            lines.Add(new DailySalesWhatsAppVanLine(
                OtherRoutesLabel, unclaimed.Sum(route => route.Sales), unclaimed.Select(route => route.RouteCode).ToList(), VanTenders(unclaimed)));
        }

        if (salesOutsideRoutes != 0m)
        {
            var split = outsideRoutesByTender ?? VanDailyTruckTenders.None;
            lines.Add(new DailySalesWhatsAppVanLine(
                NoRouteLabel, salesOutsideRoutes, [],
                VanTenders(split.Cash, split.Ecocash, split.Innbucks, split.OtherTender, split.Untendered)));
        }

        return lines;
    }

    /// <summary>The van tenders as the message names them and in the order it lists them.</summary>
    public static readonly string[] VanTenderNames = ["Cash", "EcoCash", "InnBucks", CardOrTransferLabel, NotRecordedLabel];

    /// <summary>
    /// The van app's Card and Transfer, and anything else that is not one of the three named tenders —
    /// the truck report's "other" column. Not "Swipe": a van takes bank transfers as well as cards.
    /// </summary>
    public const string CardOrTransferLabel = "Card / transfer";

    public const string NotRecordedLabel = "Not recorded";

    private static List<DailySalesWhatsAppTenderAmount> VanTenders(IReadOnlyCollection<VanDailyTruckDayDto> routes) =>
        VanTenders(
            routes.Sum(route => route.Cash),
            routes.Sum(route => route.Ecocash),
            routes.Sum(route => route.Innbucks),
            routes.Sum(route => route.OtherTender),
            routes.Sum(route => route.Untendered));

    private static List<DailySalesWhatsAppTenderAmount> VanTenders(
        decimal cash, decimal ecocash, decimal innbucks, decimal other, decimal untendered) =>
        [
            new("Cash", cash),
            new("EcoCash", ecocash),
            new("InnBucks", innbucks),
            new(CardOrTransferLabel, other),
            new(NotRecordedLabel, untendered)
        ];

    /// <summary>Every van line's tenders added up, one per tender in the usual order.</summary>
    internal static List<DailySalesWhatsAppTenderAmount> VanTotalTenders(IReadOnlyList<DailySalesWhatsAppVanLine> lines) =>
        VanTenderNames
            .Select(name => new DailySalesWhatsAppTenderAmount(
                name,
                lines.SelectMany(line => line.Tenders).Where(tender => tender.Tender == name).Sum(tender => tender.Amount)))
            .ToList();

    private async Task<DailySalesWhatsAppShop> ShopAsync(
        string warehouse,
        string heading,
        IReadOnlyList<DailySalesWhatsAppRow> rows,
        DateTime day,
        CancellationToken cancellationToken)
    {
        var next = day.AddDays(1);
        var sales = db.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.DocDate >= day && sale.DocDate < next && sale.WarehouseCode == warehouse)
            .InBusiness(SaleBusinesses.Shops);

        var takings = await sales
            .GroupBy(sale => sale.Currency)
            .Select(group => new { Currency = group.Key, Count = group.Count(), Total = group.Sum(sale => sale.TotalAmount) })
            .ToListAsync(cancellationToken);

        var quantities = (await sales
                .SelectMany(sale => sale.Lines)
                .GroupBy(line => line.ItemCode)
                .Select(group => new { ItemCode = group.Key, Quantity = group.Sum(line => line.Quantity) })
                .ToListAsync(cancellationToken))
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemCode))
            .GroupBy(item => item.ItemCode.Trim().ToUpperInvariant())
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity), StringComparer.Ordinal);

        var lines = rows
            .Select(row => new DailySalesWhatsAppProductLine(
                row.Label,
                row.Codes.Sum(code => quantities.GetValueOrDefault(code.Trim().ToUpperInvariant())),
                row.Codes))
            .ToList();

        // A sale with no currency recorded is dollars, as the van side reads it.
        var dollars = takings.Where(group => IsDollars(group.Currency)).ToList();
        var other = OtherCurrencies(takings.Select(group => ((string?)group.Currency, group.Total)));

        return new DailySalesWhatsAppShop(
            warehouse, heading, lines, dollars.Sum(group => group.Total), takings.Sum(group => group.Count), other);
    }

    /// <summary>Every shop, vending, and the two together, each by tender.</summary>
    private async Task<DailySalesWhatsAppCounters> CountersAsync(DateTime day, CancellationToken cancellationToken)
    {
        var next = day.AddDays(1);
        var daySales = db.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.DocDate >= day && sale.DocDate < next);

        var shopTakings = await TakingsAsync(daySales.InBusiness(SaleBusinesses.Shops), cancellationToken);
        var vendingTakings = await TakingsAsync(daySales.InBusiness(SaleBusinesses.Vending), cancellationToken);

        // One name per warehouse, an active shop's first, as the settings name the chosen shop.
        var shopRows = (await db.Shops.AsNoTracking()
                .Select(shop => new { shop.WarehouseCode, shop.Name, shop.IsActive })
                .ToListAsync(cancellationToken))
            .Where(shop => !string.IsNullOrWhiteSpace(shop.WarehouseCode))
            .GroupBy(shop => WarehouseKey(shop.WarehouseCode))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(shop => shop.IsActive).First(),
                StringComparer.Ordinal);

        var warehouses = shopRows.Values.Where(shop => shop.IsActive).Select(shop => WarehouseKey(shop.WarehouseCode))
            .Concat(shopTakings.Select(cell => cell.Warehouse))
            .Distinct(StringComparer.Ordinal);

        var shops = warehouses
            .Select(warehouse => Counter(
                shopRows.TryGetValue(warehouse, out var shop) ? shop.Name.Trim() : NoName(warehouse),
                warehouse.Length == 0 ? [] : [warehouse],
                shopTakings.Where(cell => cell.Warehouse == warehouse).ToList()))
            .OrderBy(counter => counter.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var vending = Counter(
            VendingLabel,
            vendingTakings.Select(cell => cell.Warehouse).Where(code => code.Length > 0).Distinct(StringComparer.Ordinal)
                .OrderBy(code => code, StringComparer.Ordinal).ToList(),
            vendingTakings);

        var vendingPartners = await VendingPartnersAsync(vendingTakings, cancellationToken);

        var total = Counter(CountersTotalLabel, [], [.. shopTakings, .. vendingTakings]);

        return new DailySalesWhatsAppCounters(shops, vendingPartners, vending, total);
    }

    /// <summary>
    /// Vending one business partner at a time: the depot's SAP account each sale was billed to.
    /// </summary>
    /// <remarks>
    /// Every depot with an active vending cashier is listed, a day without sales included, as an active
    /// shop is — so a depot that did not trade, or whose carts did not sync, shows as such. A depot is
    /// named as its latest vending sale names it; one that has never sold is named by its code.
    /// </remarks>
    private async Task<List<DailySalesWhatsAppCounter>> VendingPartnersAsync(
        List<Takings> vendingTakings,
        CancellationToken cancellationToken)
    {
        var activeDepots = (await db.Users.AsNoTracking()
                .Where(user => user.Role == ApplicationRoles.CartVendor && user.IsActive && user.AssignedBusinessPartnerCode != null)
                .Select(user => user.AssignedBusinessPartnerCode!)
                .ToListAsync(cancellationToken))
            .Select(PartnerKey)
            .Where(code => code.Length > 0);

        var partners = vendingTakings.Select(cell => cell.Partner)
            .Concat(activeDepots)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Each account's name from its latest named vending sale, so a renamed depot reads as it does now.
        var named = await db.DesktopSales.AsNoTracking()
            .InBusiness(SaleBusinesses.Vending)
            .Where(sale => sale.CardName != null && sale.CardName != "")
            .GroupBy(sale => new { sale.CardCode, sale.CardName })
            .Select(group => new { group.Key.CardCode, group.Key.CardName, LastSale = group.Max(sale => sale.Id) })
            .ToListAsync(cancellationToken);

        var names = named
            .GroupBy(sale => PartnerKey(sale.CardCode), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(sale => sale.LastSale).First().CardName!.Trim(),
                StringComparer.Ordinal);

        var labels = partners.ToDictionary(
            code => code,
            code => names.TryGetValue(code, out var name) && name.Length > 0 ? name : NoPartnerName(code),
            StringComparer.Ordinal);

        // Two accounts under one name would read as one depot twice; the code tells them apart.
        var shared = labels.Values
            .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return partners
            .Select(code =>
            {
                var takings = vendingTakings.Where(cell => cell.Partner == code).ToList();
                var label = shared.Contains(labels[code]) && code.Length > 0 ? $"{labels[code]} ({code})" : labels[code];
                return Counter(
                    label,
                    takings.Select(cell => cell.Warehouse).Where(warehouse => warehouse.Length > 0)
                        .Distinct(StringComparer.Ordinal).OrderBy(warehouse => warehouse, StringComparer.Ordinal).ToList(),
                    takings,
                    code.Length == 0 ? null : code);
            })
            .OrderBy(counter => counter.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public const string VendingLabel = "Vending";
    public const string CountersTotalLabel = "Shops & vending";

    /// <summary>The till's four tenders, as the message names them and in the order it lists them.</summary>
    public static readonly string[] Tenders = ["Cash", "EcoCash", "InnBucks", "Swipe"];

    /// <summary>One warehouse, business partner, currency and tender's sales for the day.</summary>
    internal sealed record Takings(string Warehouse, string Partner, string? Currency, string? PaymentMethod, int SalesCount, decimal Total);

    private static async Task<List<Takings>> TakingsAsync(
        IQueryable<DesktopSaleEntity> sales, CancellationToken cancellationToken) =>
        (await sales
            .GroupBy(sale => new { sale.WarehouseCode, sale.CardCode, sale.Currency, sale.PaymentMethod })
            .Select(group => new
            {
                group.Key.WarehouseCode,
                group.Key.CardCode,
                group.Key.Currency,
                group.Key.PaymentMethod,
                Count = group.Count(),
                Total = group.Sum(sale => sale.TotalAmount)
            })
            .ToListAsync(cancellationToken))
        .Select(group => new Takings(WarehouseKey(group.WarehouseCode), PartnerKey(group.CardCode), group.Currency, group.PaymentMethod, group.Count, group.Total))
        .ToList();

    /// <summary>
    /// One block of the counter section. The four till tenders are always listed, a zero included, so the
    /// reader sees that nothing came in by EcoCash rather than wondering whether it was left out; any other
    /// tender — a sale with none recorded, a legacy "Transfer" — is listed only when it has money on it.
    /// </summary>
    internal static DailySalesWhatsAppCounter Counter(
        string label, List<string> warehouses, IReadOnlyCollection<Takings> takings, string? businessPartnerCode = null)
    {
        var dollars = takings.Where(cell => IsDollars(cell.Currency)).ToList();
        var byTender = dollars
            .GroupBy(cell => TenderLabel(cell.PaymentMethod), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(cell => cell.Total), StringComparer.Ordinal);

        var tenders = Tenders
            .Select(tender => new DailySalesWhatsAppTenderAmount(tender, byTender.GetValueOrDefault(tender)))
            .Concat(byTender
                .Where(pair => !Tenders.Contains(pair.Key, StringComparer.Ordinal) && pair.Value != 0m)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new DailySalesWhatsAppTenderAmount(pair.Key, pair.Value)))
            .ToList();

        return new DailySalesWhatsAppCounter(
            label,
            warehouses,
            dollars.Sum(cell => cell.Total),
            takings.Sum(cell => cell.SalesCount),
            tenders,
            OtherCurrencies(takings.Select(cell => (cell.Currency, cell.Total))),
            businessPartnerCode);
    }

    /// <summary>
    /// The tender as the message prints it: the wallets in their own spelling, everything else as the
    /// reports name it — so legacy casings of one tender land on one line.
    /// </summary>
    internal static string TenderLabel(string? paymentMethod) => TenderTypes.ReportingName(paymentMethod) switch
    {
        TenderTypes.Ecocash => "EcoCash",
        TenderTypes.Innbucks => "InnBucks",
        var name => name
    };

    private static List<DailySalesWhatsAppCurrencyAmount> OtherCurrencies(IEnumerable<(string? Currency, decimal Total)> takings) =>
        takings
            .Where(cell => !IsDollars(cell.Currency) && cell.Total != 0m)
            .GroupBy(cell => cell.Currency!.Trim().ToUpperInvariant())
            .Select(group => new DailySalesWhatsAppCurrencyAmount(group.Key, group.Sum(cell => cell.Total)))
            .Where(amount => amount.Amount != 0m)
            .OrderBy(amount => amount.Currency, StringComparer.Ordinal)
            .ToList();

    private static string WarehouseKey(string? warehouse) => warehouse?.Trim().ToUpperInvariant() ?? string.Empty;

    private static string PartnerKey(string? cardCode) => cardCode?.Trim().ToUpperInvariant() ?? string.Empty;

    private static string NoPartnerName(string cardCode) => cardCode.Length == 0 ? "No business partner" : cardCode;

    private static string NoName(string warehouse) => warehouse.Length == 0 ? "No warehouse" : warehouse;

    private static string Heading(DailySalesWhatsAppSettings settings) =>
        settings.ShopHeading ?? $"{settings.ShopName ?? settings.ShopWarehouseCode} Sales";

    private static bool IsDollars(string? currency) =>
        string.IsNullOrWhiteSpace(currency) || string.Equals(currency.Trim(), "USD", StringComparison.OrdinalIgnoreCase);

    /// <summary>The two upcountry territories, "UPC" and "Bulawayo UPC", as the route seed names them.</summary>
    private static bool IsUpcountry(string? territory) =>
        territory?.Contains("UPC", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The label with where the trucks worked that day appended; the bare label when no area is known.</summary>
    private static string WithArea(string label, IEnumerable<VanDailyTruckDayDto> routes)
    {
        var areas = routes
            .Select(route => route.AreaName?.Trim())
            .Where(area => !string.IsNullOrEmpty(area))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return areas.Count == 0 ? label : $"{label} - {string.Join(" / ", areas)}";
    }
}
