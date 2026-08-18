using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanStockReport;

/// <summary>
/// Builds the van stock report: load, sell-through, morning-to-morning variance, dead stock and
/// expiry exposure.
/// </summary>
/// <remarks>
/// The load comes from the snapshot's <c>OriginalQuantity</c> and what sold comes from the van sales
/// facts. That pairing is deliberate and is the whole reason the report is trustworthy: the snapshot
/// is a desktop-app feature, and no van sales path decrements its running quantity, so
/// <c>AvailableQuantity</c> on a van warehouse means nothing. <c>OriginalQuantity</c> is read fresh
/// from SAP each morning and does.
///
/// Snapshot items are per batch, so everything here sums across batches to reach an item.
/// </remarks>
public sealed class GetVanStockReportHandler(
    ApplicationDbContext db
) : IRequestHandler<GetVanStockReportQuery, ErrorOr<VanStockReportResult>>
{
    /// <summary>How many item rows a variance names before it stops listing them.</summary>
    private const int TopVariancesPerPair = 10;

    /// <summary>Batches inside this window are worth a buyer's attention; beyond it they are not yet news.</summary>
    private const int ExpiryHorizonDays = 60;

    /// <summary>Rounding floor for a quantity difference, below which it is float noise rather than a variance.</summary>
    private const decimal VarianceEpsilon = 0.001m;

    public async Task<ErrorOr<VanStockReportResult>> Handle(
        GetVanStockReportQuery query,
        CancellationToken cancellationToken)
    {
        var from = query.FromDate.Date;
        var to = query.ToDate.Date;

        if (to < from)
        {
            return Error.Validation(
                "VanSalesReports.InvalidRange",
                "The end of the period cannot be before its start.");
        }

        if ((to - from).TotalDays > VanSalesFacts.MaximumDays)
        {
            return Error.Validation(
                "VanSalesReports.RangeTooWide",
                $"Choose a period of {VanSalesFacts.MaximumDays} days or fewer.");
        }

        var vans = await LoadVanWarehousesAsync(query, cancellationToken);

        if (vans.Count == 0)
        {
            return Error.Validation(
                "VanSalesReports.NoVans",
                "No van warehouse is assigned to any rep, so there is no stock to reconcile.");
        }

        var snapshots = await LoadSnapshotsAsync(vans, from, to, cancellationToken);
        var adjustments = await LoadAdjustmentsAsync(vans, from, to, cancellationToken);

        // Sales are read through the shared fact reader like every other van report, then attributed
        // to a warehouse by the line rather than the document — a line carries the warehouse it came
        // off, and a reservation header carries none at all.
        var lines = await VanSalesFactReader.LoadSaleLinesAsync(
            db, new VanSalesFactFilter(from, to), cancellationToken);

        var sold = lines
            .Where(line => line.WarehouseCode is not null && vans.Contains(line.WarehouseCode))
            .GroupBy(line => StockKey.For(line.WarehouseCode!, line.TradingDate, line.ItemCode))
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));

        var todayCat = Services.AuditService.ToCAT(DateTime.UtcNow).Date;
        var latest = snapshots.Count == 0 ? (DateTime?)null : snapshots.Max(s => s.SnapshotDate);

        var days = BuildDays(snapshots, sold, adjustments);
        var variances = BuildVariances(snapshots, sold, adjustments);
        var items = BuildItems(snapshots, sold);

        return new VanStockReportResult(
            FromDate: from,
            ToDate: to,
            DeadStockDays: query.DeadStockDays,
            Summary: BuildSummary(vans, days, items, snapshots, latest, todayCat, query.DeadStockDays),
            Days: days,
            Variances: variances,
            Items: items,
            Expiring: BuildExpiring(snapshots, todayCat),
            Quality: BuildQuality(vans, days, snapshots, variances, lines, latest, todayCat));
    }

    // ── Reads ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The warehouses that are vans: a rep is assigned to them and a depot supplies them. Read from
    /// the assignment rather than a code prefix — see the replenishment report for why.
    /// </summary>
    private async Task<HashSet<string>> LoadVanWarehousesAsync(
        GetVanStockReportQuery query,
        CancellationToken cancellationToken)
    {
        var users = await db.Users
            .AsNoTracking()
            .Where(user => user.SupplyingWarehouseCode != null && user.AssignedWarehouseCodes != null)
            .ToListAsync(cancellationToken);

        var vans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var code in users.SelectMany(user => user.GetWarehouseCodes()))
        {
            if (!string.IsNullOrWhiteSpace(code))
            {
                vans.Add(code.Trim());
            }
        }

        if (!string.IsNullOrWhiteSpace(query.VanWarehouseCode))
        {
            var wanted = query.VanWarehouseCode.Trim();
            vans = vans
                .Where(code => string.Equals(code, wanted, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return vans;
    }

    private async Task<List<SnapshotRow>> LoadSnapshotsAsync(
        HashSet<string> vans,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var headers = await db.DailyStockSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.SnapshotDate >= from
                               && snapshot.SnapshotDate <= to
                               && vans.Contains(snapshot.WarehouseCode))
            .Select(snapshot => new
            {
                snapshot.Id,
                snapshot.WarehouseCode,
                snapshot.SnapshotDate,
                snapshot.Status,
                Items = snapshot.Items
                    .Select(item => new
                    {
                        item.ItemCode,
                        item.ItemDescription,
                        item.BatchNumber,
                        item.OriginalQuantity,
                        item.ExpiryDate
                    })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        return headers
            .Select(header => new SnapshotRow(
                header.WarehouseCode,
                header.SnapshotDate.Date,
                header.Status == StockSnapshotStatus.Complete,
                header.Items
                    .Select(item => new SnapshotItem(
                        item.ItemCode.ToUpperInvariant(),
                        item.ItemDescription,
                        item.BatchNumber,
                        item.OriginalQuantity,
                        item.ExpiryDate))
                    .ToList()))
            .ToList();
    }

    private async Task<Dictionary<StockKey, decimal>> LoadAdjustmentsAsync(
        HashSet<string> vans,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var rows = await db.StockTransferAdjustments
            .AsNoTracking()
            .Where(adjustment => adjustment.SnapshotDate >= from
                                 && adjustment.SnapshotDate <= to
                                 && vans.Contains(adjustment.WarehouseCode))
            .Select(adjustment => new
            {
                adjustment.WarehouseCode,
                adjustment.SnapshotDate,
                adjustment.ItemCode,
                adjustment.AdjustmentQuantity
            })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => StockKey.For(row.WarehouseCode, row.SnapshotDate, row.ItemCode))
            .ToDictionary(group => group.Key, group => group.Sum(row => row.AdjustmentQuantity));
    }

    // ── C1: the day ─────────────────────────────────────────────────────────────

    private static List<VanStockDayResult> BuildDays(
        List<SnapshotRow> snapshots,
        Dictionary<StockKey, decimal> sold,
        Dictionary<StockKey, decimal> adjustments) =>
        snapshots
            .Select(snapshot =>
            {
                // Snapshot rows are per batch; an item is the sum of its batches.
                var byItem = snapshot.Items
                    .GroupBy(item => item.ItemCode)
                    .ToDictionary(group => group.Key, group => group.Sum(item => item.OriginalQuantity));

                decimal SoldFor(string itemCode) =>
                    sold.TryGetValue(StockKey.For(snapshot.WarehouseCode, snapshot.SnapshotDate, itemCode), out var q)
                        ? q
                        : 0m;

                // Everything that moved that day, not just what the morning listed. An item sold off
                // a van it was not snapshotted on is exactly the anomaly this report exists to show
                // — stock reached the van mid-round, or the snapshot missed it — and iterating the
                // snapshot alone dropped those sales silently, understating the day.
                var soldOnly = sold.Keys
                    .Where(key => key.WarehouseCode == snapshot.WarehouseCode.ToUpperInvariant()
                                  && key.SnapshotDate == snapshot.SnapshotDate.Date
                                  && !byItem.ContainsKey(key.ItemCode))
                    .Select(key => key.ItemCode)
                    .ToList();

                var allItems = byItem.Keys.Concat(soldOnly).ToList();
                var soldItems = allItems.Count(itemCode => SoldFor(itemCode) > 0);

                return new VanStockDayResult(
                    VanWarehouseCode: snapshot.WarehouseCode,
                    SnapshotDate: snapshot.SnapshotDate,
                    SnapshotComplete: snapshot.IsComplete,
                    ItemCount: byItem.Count,
                    LoadedQuantity: byItem.Values.Sum(),
                    SoldQuantity: allItems.Sum(SoldFor),
                    AdjustmentQuantity: allItems.Sum(itemCode =>
                        adjustments.TryGetValue(
                            StockKey.For(snapshot.WarehouseCode, snapshot.SnapshotDate, itemCode), out var q)
                            ? q
                            : 0m),
                    SoldItemCount: soldItems,
                    // Only a loaded item can be unsold. A sold-only item was never on the van's
                    // morning list, so it cannot have sat there untouched.
                    UnsoldItemCount: byItem.Keys.Count(itemCode => SoldFor(itemCode) == 0));
            })
            .OrderBy(day => day.VanWarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(day => day.SnapshotDate)
            .ToList();

    // ── C2: variance ────────────────────────────────────────────────────────────

    private static List<VanStockVarianceResult> BuildVariances(
        List<SnapshotRow> snapshots,
        Dictionary<StockKey, decimal> sold,
        Dictionary<StockKey, decimal> adjustments) =>
        snapshots
            .GroupBy(snapshot => snapshot.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .SelectMany(van =>
            {
                var ordered = van.OrderBy(snapshot => snapshot.SnapshotDate).ToList();
                var pairs = new List<VanStockVarianceResult>();

                for (var index = 1; index < ordered.Count; index++)
                {
                    var opening = ordered[index - 1];
                    var closing = ordered[index];
                    var gapDays = (int)(closing.SnapshotDate - opening.SnapshotDate).TotalDays;
                    var hasGap = gapDays != 1;

                    var openingByItem = SumByItem(opening);
                    var closingByItem = SumByItem(closing);

                    decimal Sold(string item) =>
                        sold.TryGetValue(StockKey.For(van.Key, opening.SnapshotDate, item), out var q) ? q : 0m;

                    decimal Adjustment(string item) =>
                        adjustments.TryGetValue(StockKey.For(van.Key, opening.SnapshotDate, item), out var q)
                            ? q
                            : 0m;

                    var itemVariances = hasGap
                        ? []
                        : openingByItem.Keys.Union(closingByItem.Keys, StringComparer.OrdinalIgnoreCase)
                            .Select(item =>
                            {
                                openingByItem.TryGetValue(item, out var open);
                                closingByItem.TryGetValue(item, out var close);

                                return new VanStockItemVarianceResult(
                                    ItemCode: item,
                                    ItemDescription: DescriptionOf(opening, closing, item),
                                    Expected: open - Sold(item) + Adjustment(item),
                                    Actual: close);
                            })
                            .Where(variance => Math.Abs(variance.Variance) > VarianceEpsilon)
                            .OrderByDescending(variance => Math.Abs(variance.Variance))
                            .ToList();

                    pairs.Add(new VanStockVarianceResult(
                        VanWarehouseCode: van.Key,
                        FromSnapshot: opening.SnapshotDate,
                        ToSnapshot: closing.SnapshotDate,
                        HasGap: hasGap,
                        GapDays: gapDays,
                        OpeningQuantity: openingByItem.Values.Sum(),
                        SoldQuantity: openingByItem.Keys.Sum(Sold),
                        AdjustmentQuantity: openingByItem.Keys.Sum(Adjustment),
                        ClosingQuantity: closingByItem.Values.Sum(),
                        ItemsShort: itemVariances.Count(variance => variance.Variance < 0),
                        ItemsOver: itemVariances.Count(variance => variance.Variance > 0),
                        TopVariances: itemVariances.Take(TopVariancesPerPair).ToList()));
                }

                return pairs;
            })
            .OrderBy(variance => variance.VanWarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(variance => variance.FromSnapshot)
            .ToList();

    // ── C3 / C4: sell-through and dead stock ────────────────────────────────────

    private static List<VanStockItemResult> BuildItems(
        List<SnapshotRow> snapshots,
        Dictionary<StockKey, decimal> sold) =>
        snapshots
            .SelectMany(snapshot => SumByItem(snapshot)
                .Select(item => new
                {
                    snapshot.WarehouseCode,
                    snapshot.SnapshotDate,
                    ItemCode = item.Key,
                    Loaded = item.Value,
                    Description = snapshot.Items
                        .FirstOrDefault(row => row.ItemCode == item.Key)?.ItemDescription,
                    Sold = sold.TryGetValue(
                        StockKey.For(snapshot.WarehouseCode, snapshot.SnapshotDate, item.Key), out var q)
                        ? q
                        : 0m
                }))
            .GroupBy(row => row.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new VanStockItemResult(
                ItemCode: group.Key,
                ItemDescription: group
                    .Select(row => row.Description)
                    .FirstOrDefault(description => !string.IsNullOrWhiteSpace(description)),
                VanCount: group.Select(row => row.WarehouseCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                DaysOnVan: group.Count(),
                DaysSold: group.Count(row => row.Sold > 0),
                // The dead-stock measure: loaded, carried around, and sold nothing.
                DaysOnVanWithoutSelling: group.Count(row => row.Sold == 0),
                LoadedQuantity: group.Sum(row => row.Loaded),
                SoldQuantity: group.Sum(row => row.Sold),
                LastSoldOn: group.Where(row => row.Sold > 0)
                    .Select(row => (DateTime?)row.SnapshotDate)
                    .DefaultIfEmpty(null)
                    .Max()))
            // Deadest first: most days carried, least sold.
            .OrderByDescending(item => item.DaysOnVanWithoutSelling)
            .ThenBy(item => item.SellThroughRate ?? 0)
            .ThenBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ── C5: expiry ──────────────────────────────────────────────────────────────

    private static List<VanStockExpiryResult> BuildExpiring(
        List<SnapshotRow> snapshots,
        DateTime todayCat)
    {
        // The newest snapshot per van, because expiry is a question about what is on the van now
        // rather than about what was on it a fortnight ago.
        var newest = snapshots
            .GroupBy(snapshot => snapshot.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .Select(van => van.OrderByDescending(snapshot => snapshot.SnapshotDate).First())
            .ToList();

        return newest
            .SelectMany(snapshot => snapshot.Items
                .Where(item => item.ExpiryDate.HasValue
                               && item.OriginalQuantity > 0
                               && !string.IsNullOrWhiteSpace(item.BatchNumber))
                .Select(item => new VanStockExpiryResult(
                    VanWarehouseCode: snapshot.WarehouseCode,
                    ItemCode: item.ItemCode,
                    ItemDescription: item.ItemDescription,
                    BatchNumber: item.BatchNumber!,
                    ExpiryDate: item.ExpiryDate!.Value.Date,
                    DaysToExpiry: (int)(item.ExpiryDate.Value.Date - todayCat).TotalDays,
                    Quantity: item.OriginalQuantity,
                    SnapshotDate: snapshot.SnapshotDate)))
            .Where(expiry => expiry.DaysToExpiry <= ExpiryHorizonDays)
            // Already expired first, then soonest.
            .OrderBy(expiry => expiry.DaysToExpiry)
            .ThenByDescending(expiry => expiry.Quantity)
            .ToList();
    }

    // ── Summary and quality ─────────────────────────────────────────────────────

    private static VanStockSummaryResult BuildSummary(
        HashSet<string> vans,
        List<VanStockDayResult> days,
        List<VanStockItemResult> items,
        List<SnapshotRow> snapshots,
        DateTime? latest,
        DateTime todayCat,
        int deadStockDays) =>
        new(
            VanCount: vans.Count,
            SnapshotDayCount: days.Count,
            MissingSnapshotDays: CountMissingDays(vans, snapshots),
            ItemCount: items.Count,
            // Carried on a van for the threshold and never sold once. Riding the round for a
            // fortnight is capital and shelf space that could be holding something that moves.
            DeadItemCount: items.Count(item =>
                item.DaysSold == 0 && item.DaysOnVanWithoutSelling >= deadStockDays),
            LoadedQuantity: days.Sum(day => day.LoadedQuantity),
            SoldQuantity: days.Sum(day => day.SoldQuantity),
            LatestSnapshotDate: latest,
            SnapshotAgeDays: latest is { } date ? Math.Max(0, (int)(todayCat - date).TotalDays) : null);

    private static VanStockQualityResult BuildQuality(
        HashSet<string> vans,
        List<VanStockDayResult> days,
        List<SnapshotRow> snapshots,
        List<VanStockVarianceResult> variances,
        List<VanSaleLineFact> lines,
        DateTime? latest,
        DateTime todayCat)
    {
        var withSnapshots = snapshots
            .Select(snapshot => snapshot.WarehouseCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new VanStockQualityResult(
            MissingSnapshotDays: CountMissingDays(vans, snapshots),
            IncompleteSnapshots: snapshots.Count(snapshot => !snapshot.IsComplete),
            VansWithNoSnapshot: vans.Count(van => !withSnapshots.Contains(van)),
            VariancePairsSkippedForGaps: variances.Count(variance => variance.HasGap),
            SalesForWarehousesWithNoSnapshot: lines.Count(line =>
                line.WarehouseCode is not null
                && vans.Contains(line.WarehouseCode)
                && !withSnapshots.Contains(line.WarehouseCode)),
            LatestSnapshotDate: latest,
            SnapshotAgeDays: latest is { } date ? Math.Max(0, (int)(todayCat - date).TotalDays) : null);
    }

    /// <summary>
    /// Van-days between a van's first and last snapshot that have no snapshot of their own. Counted
    /// inside each van's own observed span, so a van that only came on the system halfway through the
    /// period is not charged for the days before it existed.
    /// </summary>
    private static int CountMissingDays(HashSet<string> vans, List<SnapshotRow> snapshots) =>
        snapshots
            .GroupBy(snapshot => snapshot.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .Sum(van =>
            {
                var dates = van.Select(snapshot => snapshot.SnapshotDate).Distinct().OrderBy(date => date).ToList();
                var span = (int)(dates[^1] - dates[0]).TotalDays + 1;
                return span - dates.Count;
            });

    private static Dictionary<string, decimal> SumByItem(SnapshotRow snapshot) =>
        snapshot.Items
            .GroupBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.OriginalQuantity),
                StringComparer.OrdinalIgnoreCase);

    private static string? DescriptionOf(SnapshotRow opening, SnapshotRow closing, string itemCode) =>
        opening.Items.Concat(closing.Items)
            .Where(item => string.Equals(item.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.ItemDescription)
            .FirstOrDefault(description => !string.IsNullOrWhiteSpace(description));

    /// <summary>
    /// Joins a sale line to the snapshot it came off.
    /// </summary>
    /// <remarks>
    /// Both string halves are normalised by <see cref="For"/> rather than trusted as they arrive. A
    /// record struct compares strings ordinally, but every set that feeds this key matches
    /// case-insensitively — the van set, the snapshot grouping, the item grouping. Building the key
    /// from raw values let a line whose warehouse differed only in case pass the van filter and then
    /// miss its snapshot, dropping that day's sales with nothing to show for it.
    /// </remarks>
    private readonly record struct StockKey(string WarehouseCode, DateTime SnapshotDate, string ItemCode)
    {
        public static StockKey For(string warehouseCode, DateTime snapshotDate, string itemCode) =>
            new(warehouseCode.ToUpperInvariant(), snapshotDate.Date, itemCode.ToUpperInvariant());
    }

    private sealed record SnapshotRow(
        string WarehouseCode,
        DateTime SnapshotDate,
        bool IsComplete,
        List<SnapshotItem> Items);

    private sealed record SnapshotItem(
        string ItemCode,
        string? ItemDescription,
        string? BatchNumber,
        decimal OriginalQuantity,
        DateTime? ExpiryDate);
}
