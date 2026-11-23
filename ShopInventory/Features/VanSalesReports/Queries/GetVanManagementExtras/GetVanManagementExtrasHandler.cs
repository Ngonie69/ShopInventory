using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries.GetVanDailyTruckReport;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanManagementExtras;

public sealed class GetVanManagementExtrasHandler(
    ApplicationDbContext db,
    IOptions<TaxSettings> tax
) : IRequestHandler<GetVanManagementExtrasQuery, ErrorOr<VanManagementExtrasResult>>
{
    public async Task<ErrorOr<VanManagementExtrasResult>> Handle(
        GetVanManagementExtrasQuery query,
        CancellationToken cancellationToken)
    {
        var from = query.FromDate.Date;
        var to = query.ToDate.Date;

        if (to < from)
        {
            return Errors.VanTruckDays.InvalidRange;
        }

        if ((to - from).TotalDays >= GetVanDailyTruckReportHandler.MaximumDays)
        {
            return Errors.VanTruckDays.RangeTooWide(GetVanDailyTruckReportHandler.MaximumDays);
        }

        var wanted = query.ItemCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var routes = await RouteAttribution.LoadAsync(db, from, to, cancellationToken);

        // Every dollar line of the period: the chosen products are counted from them, and the
        // breakages are valued at what the vans sold each item for.
        var lines = (await VanSalesFactReader.LoadSaleLinesAsync(
                db, tax.Value, new VanSalesFactFilter(from, to), cancellationToken))
            .Where(IsDollars)
            .ToList();

        var products = new List<VanProductDayDto>();
        var outsideRoutes = 0m;

        foreach (var group in lines
                     .Where(line => wanted.Contains(Code(line.ItemCode)))
                     .GroupBy(line => (Route: routes.RouteFor(line.UserId, line.TradingDate), line.TradingDate, Item: Code(line.ItemCode))))
        {
            var units = group.Sum(line => line.Quantity);

            if (group.Key.Route is not { } routeId)
            {
                outsideRoutes += units;
                continue;
            }

            products.Add(new VanProductDayDto(
                routeId,
                group.Key.TradingDate,
                group.Key.Item,
                group.Select(line => line.ItemDescription).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                units,
                group.Sum(line => line.LineTotal)));
        }

        // An item's price before VAT over the period, per unit sold. Only where the quantities are in
        // one unit: an average across a case and a single would value a broken single at a case.
        var netPrice = lines
            .GroupBy(line => Code(line.ItemCode))
            .Where(item => item.Select(line => line.UoMCode ?? string.Empty).Distinct().Count() == 1
                           && item.Sum(line => line.Quantity) > 0)
            .ToDictionary(
                item => item.Key,
                item => item.Sum(line => line.NetLineTotal) / item.Sum(line => line.Quantity));

        var (startUtc, endUtc) = VanSalesFacts.ToUtcWindow(from, to);

        var breakageLines = await db.MarketBreakageLines
            .AsNoTracking()
            .Where(line => line.Breakage.Status != MarketBreakageStatuses.Rejected
                           && line.Breakage.CapturedAtUtc >= startUtc
                           && line.Breakage.CapturedAtUtc < endUtc)
            .Select(line => new
            {
                line.BreakageId,
                line.Breakage.ReportedByUserId,
                line.Breakage.ReportedByName,
                line.Breakage.CardName,
                line.Breakage.CapturedAtUtc,
                line.Breakage.Status,
                line.LineNum,
                line.ItemCode,
                line.ItemDescription,
                line.Reason,
                line.ReportedQuantity,
                line.ConfirmedQuantity
            })
            .ToListAsync(cancellationToken);

        var breakages = breakageLines
            .Select(line =>
            {
                var day = VanSalesFacts.TradingDayOf(line.CapturedAtUtc);
                var units = line.ConfirmedQuantity ?? line.ReportedQuantity;

                return new
                {
                    line.LineNum,
                    Dto = new VanBreakageLineDto(
                        line.BreakageId,
                        routes.RouteFor(line.ReportedByUserId, day),
                        day,
                        line.ReportedByName,
                        string.IsNullOrWhiteSpace(line.CardName) ? null : line.CardName,
                        Code(line.ItemCode),
                        line.ItemDescription,
                        string.IsNullOrWhiteSpace(line.Reason) ? null : line.Reason.Trim(),
                        units,
                        line.ConfirmedQuantity.HasValue,
                        line.Status,
                        netPrice.TryGetValue(Code(line.ItemCode), out var price)
                            ? decimal.Round(units * price, 2, MidpointRounding.AwayFromZero)
                            : null)
                };
            })
            .OrderBy(line => line.Dto.TradingDate)
            .ThenBy(line => line.Dto.BreakageId)
            .ThenBy(line => line.LineNum)
            .Select(line => line.Dto)
            .ToList();

        return new VanManagementExtrasResult(
            from,
            to,
            products
                .OrderBy(product => product.TradingDate)
                .ThenBy(product => product.RouteId)
                .ThenBy(product => product.ItemCode, StringComparer.Ordinal)
                .ToList(),
            breakages,
            outsideRoutes);
    }

    private static string Code(string itemCode) => itemCode.Trim().ToUpperInvariant();

    /// <summary>A line that names no currency is a dollar line, as on the daily truck report.</summary>
    private static bool IsDollars(VanSaleLineFact line) =>
        string.IsNullOrWhiteSpace(line.Currency) || string.Equals(line.Currency.Trim(), "USD", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The route a rep's sale or report belongs to on a day, by the daily truck report's rule: the route
    /// the truck's day was opened on by either rep, else the route the truck's reps are assigned to.
    /// </summary>
    private sealed class RouteAttribution(VanTruckRoutes trucks, IReadOnlyDictionary<(string Truck, DateTime Day), int> dayRoutes)
    {
        public static async Task<RouteAttribution> LoadAsync(
            ApplicationDbContext db, DateTime from, DateTime to, CancellationToken cancellationToken)
        {
            var trucks = await VanTruckRoutes.LoadAsync(db, cancellationToken);

            var days = await db.VanRouteDays
                .AsNoTracking()
                .Where(day => day.TradingDate >= from && day.TradingDate <= to && day.RouteId != null)
                .Select(day => new { day.Id, day.UserId, day.TradingDate, day.RouteId })
                .ToListAsync(cancellationToken);

            var dayRoutes = days
                .GroupBy(day => (Truck: trucks.TruckOf(day.UserId), Day: day.TradingDate.Date))
                .ToDictionary(group => group.Key, group => group.OrderBy(day => day.Id).First().RouteId!.Value);

            return new RouteAttribution(trucks, dayRoutes);
        }

        public int? RouteFor(Guid userId, DateTime day)
        {
            var truck = trucks.TruckOf(userId);

            return dayRoutes.TryGetValue((truck, day.Date), out var route) ? route : trucks.RouteOf(truck);
        }
    }
}
