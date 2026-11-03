using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppSettings;
using ShopInventory.Features.VanSalesReports.Queries.GetVanDailyTruckReport;

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
        var vanLines = VanLines(days, settings.Value.VanRows, trucks.Value.SalesOutsideRoutes);
        var vanOther = days
            .Where(row => row.OtherCurrencySales is { } amount && amount != 0m)
            .GroupBy(row => row.OtherCurrencyCodes ?? "Other")
            .Select(group => new DailySalesWhatsAppCurrencyAmount(group.Key, group.Sum(row => row.OtherCurrencySales!.Value)))
            .OrderBy(amount => amount.Currency, StringComparer.Ordinal)
            .ToList();

        var shop = settings.Value.ShopWarehouseCode is { } warehouse
            ? await ShopAsync(warehouse, Heading(settings.Value), settings.Value.ProductRows, day, cancellationToken)
            : null;

        var vanTotal = vanLines.Sum(line => line.Sales);
        var message = DailySalesWhatsAppMessage.Build(day, vanLines, vanTotal, vanOther, shop);

        return new DailySalesWhatsAppReport(
            day, vanLines, vanTotal, vanOther, shop, message, (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);
    }

    /// <summary>The van table's lines for one day's rows of the daily truck report.</summary>
    internal static List<DailySalesWhatsAppVanLine> VanLines(
        IReadOnlyList<VanDailyTruckDayDto> days,
        IReadOnlyList<DailySalesWhatsAppRow> rows,
        decimal salesOutsideRoutes)
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
                lines.Add(new DailySalesWhatsAppVanLine(label, route.Sales, [route.RouteCode]));
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
                lines.Add(new DailySalesWhatsAppVanLine(label, matched.Sum(route => route.Sales), row.Codes));
            }
        }

        var unclaimed = days.Where(route => !claimed.Contains(route.RouteCode) && route.Sales != 0m).ToList();
        if (unclaimed.Count > 0)
        {
            lines.Add(new DailySalesWhatsAppVanLine(
                OtherRoutesLabel, unclaimed.Sum(route => route.Sales), unclaimed.Select(route => route.RouteCode).ToList()));
        }

        if (salesOutsideRoutes != 0m)
        {
            lines.Add(new DailySalesWhatsAppVanLine(NoRouteLabel, salesOutsideRoutes, []));
        }

        return lines;
    }

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
        var other = takings
            .Where(group => !IsDollars(group.Currency) && group.Total != 0m)
            .GroupBy(group => group.Currency!.Trim().ToUpperInvariant())
            .Select(group => new DailySalesWhatsAppCurrencyAmount(group.Key, group.Sum(item => item.Total)))
            .OrderBy(amount => amount.Currency, StringComparer.Ordinal)
            .ToList();

        return new DailySalesWhatsAppShop(
            warehouse, heading, lines, dollars.Sum(group => group.Total), takings.Sum(group => group.Count), other);
    }

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
