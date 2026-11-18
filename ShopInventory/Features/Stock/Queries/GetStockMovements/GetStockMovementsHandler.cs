using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Stock.Queries.GetStockMovements;

/// <summary>
/// Reads the journal behind <see cref="GetStockAsAt.GetStockAsAtHandler"/>, oldest first.
/// </summary>
public sealed class GetStockMovementsHandler(
    ApplicationDbContext context,
    IOptions<DailyStockSettings> dailyStock
) : IRequestHandler<GetStockMovementsQuery, ErrorOr<StockMovementsResult>>
{
    /// <summary>A shop's busiest day journals a few thousand movements; a page draws far fewer.</summary>
    public const int MaxMovements = 2000;

    /// <summary>The widest window one read may ask for.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(31);

    public async Task<ErrorOr<StockMovementsResult>> Handle(
        GetStockMovementsQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.WarehouseCode))
            return Error.Validation("Stock.WarehouseRequired", "Name the warehouse to read.");

        var from = DateTime.SpecifyKind(query.FromUtc, DateTimeKind.Utc);
        var to = DateTime.SpecifyKind(query.ToUtc, DateTimeKind.Utc);
        if (to <= from)
            return Error.Validation("Stock.MovementWindow", "The end of the window must come after its start.");
        if (to - from > MaxWindow)
            return Error.Validation("Stock.MovementWindow", $"Ask for at most {MaxWindow.TotalDays:0} days of movements at a time.");

        var warehouse = query.WarehouseCode.Trim();
        var item = string.IsNullOrWhiteSpace(query.ItemCode) ? null : query.ItemCode.Trim();

        var rows = context.StockMovements
            .AsNoTracking()
            .Where(m => m.WarehouseCode == warehouse && m.OccurredAt > from && m.OccurredAt <= to);
        if (item is not null)
            rows = rows.Where(m => m.ItemCode == item);

        var page = await rows
            .OrderBy(m => m.OccurredAt)
            .ThenBy(m => m.Id)
            .Take(MaxMovements + 1)
            .ToListAsync(cancellationToken);

        var movements = page
            .Take(MaxMovements)
            .Select(m => new StockMovementDto(
                m.Id,
                DateTime.SpecifyKind(m.OccurredAt, DateTimeKind.Utc),
                m.LedgerDay,
                m.Kind,
                m.ItemCode,
                m.Quantity,
                m.BalanceAfter,
                m.Reference,
                m.DocumentKey))
            .ToList();

        var openings = item is null
            ? []
            : await OpeningsAsync(warehouse, item, from, to, cancellationToken);

        return new StockMovementsResult(warehouse, from, to, page.Count > MaxMovements, movements, openings);
    }

    private async Task<List<StockDayOpeningDto>> OpeningsAsync(
        string warehouse, string item, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var fetchTime = StockLedgerDay.ParseFetchTime(dailyStock.Value.StockFetchTimeCAT);
        // The day in force at the window's start opened before it, so it is not an opening inside it.
        var firstDay = StockLedgerDay.Resolve(from, fetchTime).AddDays(1);
        var lastDay = StockLedgerDay.Resolve(to, fetchTime);
        if (lastDay < firstDay) return [];

        var snapshots = await context.DailyStockSnapshots
            .AsNoTracking()
            .Where(s => s.WarehouseCode == warehouse
                        && s.Status == StockSnapshotStatus.Complete
                        && s.SnapshotDate >= firstDay
                        && s.SnapshotDate <= lastDay)
            .Select(s => new { s.Id, s.SnapshotDate, TakenAt = s.CompletedAt ?? s.CreatedAt })
            .OrderBy(s => s.SnapshotDate)
            .ToListAsync(cancellationToken);
        if (snapshots.Count == 0) return [];

        var ids = snapshots.Select(s => s.Id).ToList();
        var rows = await context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(i => ids.Contains(i.SnapshotId) && i.ItemCode == item)
            .Select(i => new { i.SnapshotId, i.OriginalQuantity })
            .ToListAsync(cancellationToken);
        var bySnapshot = rows
            .GroupBy(r => r.SnapshotId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.OriginalQuantity));

        return snapshots
            .Select(s => new StockDayOpeningDto(
                s.SnapshotDate,
                DateTime.SpecifyKind(s.TakenAt, DateTimeKind.Utc),
                bySnapshot.GetValueOrDefault(s.Id)))
            .ToList();
    }
}
