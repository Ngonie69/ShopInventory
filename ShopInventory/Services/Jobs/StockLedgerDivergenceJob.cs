using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Asks, every hour, whether the stock ledger still agrees with SAP.
/// </summary>
/// <remarks>
/// The ledger is the morning snapshot less everything this system has promised since, so it is only
/// right about stock this system moved. A goods issue done in the SAP client, a manual adjustment, an
/// invoice raised outside this application — none of it reaches the ledger, and the gap grows
/// silently until the next morning's fetch papers over it. Nothing else would ever notice.
///
/// <para><b>Reports, never corrects.</b> Writing SAP's figure back into the ledger would hide the
/// only thing worth knowing here: that something is moving stock this system cannot see. A
/// divergence is a question for a person, and the count over time says whether the ledger can be
/// trusted at all — which is what makes it the instrument the remaining work is measured
/// against.</para>
///
/// <para><b>Only what moved.</b> Comparing every item in every monitored warehouse would be the
/// whole-warehouse scan this codebase has repeatedly optimised away: there are six process-wide SAP
/// slots and a stock read is what fills them, so an hourly full sweep would starve the interactive
/// users it exists to protect. Divergence can only appear where a quantity changed, so the job asks
/// SAP about the rows the day has touched and no others. On a quiet warehouse that is nothing at
/// all.</para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class StockLedgerDivergenceJob(
    IServiceProvider serviceProvider,
    IOptions<DailyStockSettings> dailyStock,
    ILogger<StockLedgerDivergenceJob> logger) : IJob
{
    /// <summary>
    /// Below this, a difference is rounding rather than a movement. Quantities are stored to six
    /// decimal places and weighed goods divide unevenly.
    /// </summary>
    private const decimal Tolerance = 0.0001m;

    /// <summary>
    /// Most rows checked in one pass, so a bad day cannot turn this into the sweep it avoids.
    /// </summary>
    private const int MaxRowsPerRun = 500;

    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var sapClient = scope.ServiceProvider.GetRequiredService<ISAPServiceLayerClient>();
        var ledger = scope.ServiceProvider.GetRequiredService<IStockLedger>();

        var ledgerDay = ledger.CurrentLedgerDay;
        var recorded = 0;
        var compared = 0;

        foreach (var warehouseCode in dailyStock.Value.MonitoredWarehouses)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var moved = await MovedTodayAsync(db, ledgerDay, warehouseCode, context.CancellationToken);
                if (moved.Count == 0)
                {
                    continue;
                }

                var sapStock = await sapClient.GetStockQuantitiesForItemsInWarehouseAsync(
                    warehouseCode,
                    moved.Keys,
                    context.CancellationToken);

                var sapByItem = sapStock
                    .Where(stock => !string.IsNullOrWhiteSpace(stock.ItemCode))
                    .ToDictionary(stock => stock.ItemCode!, stock => stock, StringComparer.OrdinalIgnoreCase);

                foreach (var (itemCode, ledgerQuantity) in moved)
                {
                    if (!sapByItem.TryGetValue(itemCode, out var stock))
                    {
                        // SAP does not carry the item in this warehouse at all. That is a divergence
                        // in its own right, but it is also what a decommissioned item looks like, so
                        // it is left to the morning fetch rather than reported every hour.
                        continue;
                    }

                    compared++;

                    var difference = ledgerQuantity - stock.Issuable;
                    if (Math.Abs(difference) <= Tolerance)
                    {
                        continue;
                    }

                    db.StockLedgerDivergences.Add(new StockLedgerDivergenceEntity
                    {
                        LedgerDay = ledgerDay,
                        WarehouseCode = warehouseCode,
                        ItemCode = itemCode,
                        LedgerQuantity = ledgerQuantity,
                        SapIssuableQuantity = stock.Issuable,
                        Difference = difference
                    });

                    recorded++;

                    logger.LogWarning(
                        "Stock ledger and SAP disagree on {ItemCode} in {WarehouseCode}: ledger {Ledger}, "
                        + "SAP {Sap}, difference {Difference}. {Direction}",
                        itemCode, warehouseCode, ledgerQuantity, stock.Issuable, difference,
                        difference > 0
                            ? "The ledger is promising stock SAP does not have."
                            : "The ledger is refusing sales SAP could cover.");
                }
            }
            catch (Exception ex)
            {
                // One warehouse failing must not stop the rest. This job reads SAP, and SAP being
                // unavailable is exactly the condition under which the other guards are already
                // degrading — it is not worth escalating twice.
                logger.LogWarning(ex,
                    "Could not compare the stock ledger against SAP for warehouse {WarehouseCode}",
                    warehouseCode);
            }
        }

        if (recorded > 0)
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }

        logger.LogInformation(
            "Stock ledger comparison for {LedgerDay:yyyy-MM-dd}: {Compared} item(s) checked, {Recorded} divergence(s) recorded",
            ledgerDay, compared, recorded);
    }

    /// <summary>
    /// Today's ledger rows whose quantity has changed since the morning fetch, summed per item.
    /// </summary>
    /// <remarks>
    /// A row still at its original quantity has had nothing happen to it that this system knows
    /// about, and asking SAP about it would only find divergence caused elsewhere — which is real,
    /// but is what the morning fetch is for. Restricting the question to what moved is what keeps
    /// this off the SAP slot pool.
    /// </remarks>
    private static async Task<Dictionary<string, decimal>> MovedTodayAsync(
        ApplicationDbContext db,
        DateTime ledgerDay,
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        var touchedItems = await db.DailyStockSnapshotItems
            .Where(row => row.Snapshot.SnapshotDate == ledgerDay
                       && row.WarehouseCode == warehouseCode
                       && row.AvailableQuantity != row.OriginalQuantity)
            .Select(row => row.ItemCode)
            .Distinct()
            .Take(MaxRowsPerRun)
            .ToListAsync(cancellationToken);

        if (touchedItems.Count == 0)
        {
            return [];
        }

        // The comparison is per item, not per batch: SAP's issuable figure is an item total, and the
        // ledger's batch rows have to be added up to face it.
        var totals = await db.DailyStockSnapshotItems
            .Where(row => row.Snapshot.SnapshotDate == ledgerDay
                       && row.WarehouseCode == warehouseCode
                       && touchedItems.Contains(row.ItemCode))
            .GroupBy(row => row.ItemCode)
            .Select(group => new { ItemCode = group.Key, Available = group.Sum(row => row.AvailableQuantity) })
            .ToListAsync(cancellationToken);

        return totals.ToDictionary(
            total => total.ItemCode,
            total => total.Available,
            StringComparer.OrdinalIgnoreCase);
    }
}
