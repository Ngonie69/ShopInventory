using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Stock.Queries.GetStockAsAt;

/// <summary>
/// Rebuilds a warehouse's on-hand at a moment: the snapshot day in force then, its opening figure
/// per item, and the balance the last journalled movement at or before the moment left.
/// </summary>
/// <remarks>
/// <para><b>The balance, not a sum.</b> Each movement carries <c>BalanceAfter</c>, the figure across
/// every batch row once it was applied. Taking the last one is right even where something moved the
/// rows without journalling, which a sum of <c>Quantity</c> over the opening would miss.</para>
///
/// <para><b>A moment whose day has no finished snapshot</b> — the minutes while the morning fetch is
/// still running — falls back to the latest finished one before it. That is the snapshot the tills
/// were selling from at that moment, so its movements up to the moment are yesterday's close.</para>
/// </remarks>
public sealed class GetStockAsAtHandler(
    ApplicationDbContext context,
    IOptions<DailyStockSettings> dailyStock,
    TimeProvider? clock = null
) : IRequestHandler<GetStockAsAtQuery, ErrorOr<StockAsAtResult>>
{
    public async Task<ErrorOr<StockAsAtResult>> Handle(GetStockAsAtQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.WarehouseCode))
            return Error.Validation("Stock.WarehouseRequired", "Name the warehouse to read.");

        var warehouse = query.WarehouseCode.Trim();
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var asAt = DateTime.SpecifyKind(query.AsAtUtc, DateTimeKind.Utc);
        if (asAt > now) asAt = now;

        var fetchTime = StockLedgerDay.ParseFetchTime(dailyStock.Value.StockFetchTimeCAT);
        var day = StockLedgerDay.Resolve(asAt, fetchTime);

        var recordedFrom = await RecordedFromAsync(warehouse, cancellationToken);

        var snapshot = await context.DailyStockSnapshots
            .AsNoTracking()
            .Where(s => s.WarehouseCode == warehouse
                        && s.SnapshotDate <= day
                        && s.Status == StockSnapshotStatus.Complete)
            .OrderByDescending(s => s.SnapshotDate)
            .FirstOrDefaultAsync(cancellationToken);

        // A snapshot from days before the moment does not describe it: the warehouse simply was not
        // fetched that day, and its rows would present a stale morning as the moment's stock.
        if (snapshot is null || snapshot.SnapshotDate < day.AddDays(-1)
            || (snapshot.SnapshotDate < day && !await IsFetchingAsync(warehouse, day, cancellationToken)))
        {
            return new StockAsAtResult(warehouse, asAt, false, day, null, recordedFrom, []);
        }

        // Batch rows, folded per item here rather than in SQL: a snapshot is a few hundred rows.
        var batchRows = await context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(i => i.SnapshotId == snapshot.Id)
            .Select(i => new { i.ItemCode, i.ItemDescription, i.OriginalQuantity })
            .ToListAsync(cancellationToken);

        var openings = batchRows
            .GroupBy(i => i.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                ItemCode = g.Key,
                Name = g.Select(i => i.ItemDescription).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                Opening = g.Sum(i => i.OriginalQuantity)
            })
            .ToList();

        var ledgerDay = snapshot.SnapshotDate;
        var movements = await context.StockMovements
            .AsNoTracking()
            .Where(m => m.LedgerDay == ledgerDay && m.WarehouseCode == warehouse && m.OccurredAt <= asAt)
            .Select(m => new { m.Id, m.ItemCode, m.OccurredAt, m.BalanceAfter })
            .ToListAsync(cancellationToken);

        var lastByItem = movements
            .GroupBy(m => m.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(m => m.OccurredAt).ThenBy(m => m.Id).Last(),
                StringComparer.OrdinalIgnoreCase);

        var items = new Dictionary<string, StockAsAtItemDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var opening in openings)
        {
            lastByItem.TryGetValue(opening.ItemCode, out var last);
            items[opening.ItemCode] = new StockAsAtItemDto(
                opening.ItemCode,
                opening.Name,
                last?.BalanceAfter ?? opening.Opening,
                opening.Opening,
                last is null ? null : DateTime.SpecifyKind(last.OccurredAt, DateTimeKind.Utc));
        }

        // An item the morning fetch did not hold but a movement brought in later that day.
        foreach (var (itemCode, last) in lastByItem)
        {
            if (items.ContainsKey(itemCode)) continue;
            items[itemCode] = new StockAsAtItemDto(
                itemCode, null, last.BalanceAfter, 0m, DateTime.SpecifyKind(last.OccurredAt, DateTimeKind.Utc));
        }

        return new StockAsAtResult(
            warehouse,
            asAt,
            true,
            ledgerDay,
            DateTime.SpecifyKind(snapshot.CompletedAt ?? snapshot.CreatedAt, DateTimeKind.Utc),
            recordedFrom,
            items.Values.OrderBy(i => i.ItemCode, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>The day's own snapshot exists but is not finished yet.</summary>
    private Task<bool> IsFetchingAsync(string warehouse, DateTime day, CancellationToken cancellationToken) =>
        context.DailyStockSnapshots.AsNoTracking().AnyAsync(
            s => s.WarehouseCode == warehouse && s.SnapshotDate == day && s.Status == StockSnapshotStatus.Pending,
            cancellationToken);

    /// <summary>
    /// The first day this warehouse can be rebuilt for: the first finished snapshot on or after the
    /// first journalled movement. Snapshots older than the journal hold an opening but nothing to say
    /// what happened after it.
    /// </summary>
    private async Task<DateTime?> RecordedFromAsync(string warehouse, CancellationToken cancellationToken)
    {
        var firstMovementDay = await context.StockMovements
            .AsNoTracking()
            .Where(m => m.WarehouseCode == warehouse)
            .OrderBy(m => m.LedgerDay)
            .Select(m => (DateTime?)m.LedgerDay)
            .FirstOrDefaultAsync(cancellationToken);

        if (firstMovementDay is null) return null;

        return await context.DailyStockSnapshots
            .AsNoTracking()
            .Where(s => s.WarehouseCode == warehouse
                        && s.Status == StockSnapshotStatus.Complete
                        && s.SnapshotDate >= firstMovementDay)
            .OrderBy(s => s.SnapshotDate)
            .Select(s => (DateTime?)s.SnapshotDate)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
