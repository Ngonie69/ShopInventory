using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.Reports.Queries.GetNegativeStockTrend;

public sealed class GetNegativeStockTrendHandler(ApplicationDbContext context)
    : IRequestHandler<GetNegativeStockTrendQuery, ErrorOr<NegativeStockTrendDto>>
{
    private const int MaxDays = 365;

    public async Task<ErrorOr<NegativeStockTrendDto>> Handle(
        GetNegativeStockTrendQuery query,
        CancellationToken cancellationToken)
    {
        var days = Math.Clamp(query.Days, 1, MaxDays);
        var from = DateTime.UtcNow.Date.AddDays(-days);

        var rows = await context.NegativeStockObservations
            .AsNoTracking()
            .Where(row => row.ObservedOn >= from)
            .Select(row => new { row.ObservedOn, row.WarehouseCode, row.OnHand })
            .ToListAsync(cancellationToken);

        // Summed in memory: the table holds only what is actually negative, which on a healthy
        // company is nothing and on a bad one is hundreds — not a size worth a grouped query.
        var history = rows
            .GroupBy(row => row.ObservedOn)
            .Select(group => new NegativeStockDayDto(
                group.Key,
                group.Count(),
                group.Select(row => row.WarehouseCode).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                // Reported positive. "412 units below zero" is a figure people can compare; "-412"
                // invites an argument about which direction is better.
                Math.Abs(group.Sum(row => row.OnHand))))
            .OrderBy(day => day.ObservedOn)
            .ToList();

        var latest = history.LastOrDefault();

        var worst = latest is null
            ? []
            : rows
                .Where(row => row.ObservedOn == latest.ObservedOn)
                .GroupBy(row => row.WarehouseCode, StringComparer.OrdinalIgnoreCase)
                .Select(group => new NegativeStockWarehouseDto(
                    group.Key,
                    group.Count(),
                    Math.Abs(group.Sum(row => row.OnHand))))
                .OrderByDescending(warehouse => warehouse.UnitsBelowZero)
                .Take(10)
                .ToList();

        return new NegativeStockTrendDto(days, latest, history, worst);
    }
}
