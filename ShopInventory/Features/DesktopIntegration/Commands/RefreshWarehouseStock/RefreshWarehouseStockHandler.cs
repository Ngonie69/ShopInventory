using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.RefreshWarehouseStock;

/// <summary>
/// Moves every item in one warehouse's ledger to SAP's figure, less the till sales SAP has not been
/// given yet.
/// </summary>
/// <remarks>
/// <para><b>The same correction the hourly job makes, over the whole warehouse.</b> The arithmetic,
/// the bounds and the row writes are <see cref="StockLedgerDivergenceJob"/>'s own, called rather than
/// copied, so a button press and the hourly pass cannot disagree about what "in step" means. What
/// differs is only which items are asked about: the job asks about rows that moved, this asks about
/// all of them, which is the case a goods receipt booked straight into SAP falls through.</para>
///
/// <para><b>Not the morning fetch again.</b> Rebuilding the snapshot from SAP would hand back every
/// unit the tills have sold since 07:00 that has not reached SAP yet. The target here is SAP's
/// issuable quantity less those sales, as it is for the job.</para>
///
/// <para><b>Nothing is recorded about the change.</b> No movement, adjustment or divergence row: the
/// rows are moved and the change is logged. That is deliberate — a receipt is SAP's document, and
/// this is only catching the ledger up with it.</para>
///
/// <para><b>Refuses rather than guesses.</b> Vans and any warehouse outside
/// <see cref="DailyStockSettings.ReconcileWarehouses"/> are refused, because a van's row is its
/// morning load and correcting it destroys the van reconciliation. So is a warehouse with no finished
/// snapshot today, and a SAP read that comes back empty — OITW holds a row for every item a warehouse
/// has ever carried, so empty is a failed read, and acting on it would zero the shop.</para>
/// </remarks>
public sealed class RefreshWarehouseStockHandler(
    ApplicationDbContext db,
    ISAPServiceLayerClient sapClient,
    IStockLedger ledger,
    IOptions<DailyStockSettings> dailyStock,
    ILogger<RefreshWarehouseStockHandler> logger
) : IRequestHandler<RefreshWarehouseStockCommand, ErrorOr<RefreshWarehouseStockResult>>
{
    /// <summary>Matches the job: below this a difference is rounding, not stock.</summary>
    private const decimal Tolerance = 0.0001m;

    public async Task<ErrorOr<RefreshWarehouseStockResult>> Handle(
        RefreshWarehouseStockCommand command,
        CancellationToken cancellationToken)
    {
        var settings = dailyStock.Value;
        var warehouseCode = settings.ReconcileWarehouses.FirstOrDefault(code =>
            string.Equals(code, command.WarehouseCode?.Trim(), StringComparison.OrdinalIgnoreCase));

        if (!settings.ReconcileLedgerAgainstSap)
        {
            return Error.Conflict(
                "Stock.RefreshDisabled",
                "Refreshing stock from SAP is switched off (DailyStock:ReconcileLedgerAgainstSap).");
        }

        if (warehouseCode is null)
        {
            return Error.Conflict(
                "Stock.RefreshNotAllowed",
                $"{command.WarehouseCode} cannot be refreshed from SAP. Only the shop warehouses in "
                + "DailyStock:ReconcileWarehouses can be — a van's figure is its morning load.");
        }

        var ledgerDay = ledger.CurrentLedgerDay;

        var snapshotId = await db.DailyStockSnapshots
            .Where(header => header.SnapshotDate == ledgerDay
                          && header.WarehouseCode == warehouseCode
                          && header.Status == StockSnapshotStatus.Complete)
            .Select(header => (int?)header.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (snapshotId is null)
        {
            return Error.Conflict(
                "Stock.NoSnapshotToRefresh",
                $"{warehouseCode} has no finished snapshot for {ledgerDay:dd MMM yyyy}. "
                + "Fetch today's stock first.");
        }

        // Before SAP, as in the job: a sale that posts between the two reads is then counted twice for
        // this press, which refuses a sale, rather than in neither, which puts its units back.
        var outstanding = await UnpostedTillSales.OutstandingAsync(
            db, warehouseCode, ledgerDay, settings.StockFetchTimeCAT,
            settings.UnpostedSaleLookbackDays, logger, cancellationToken);

        List<BatchNumber> batches;
        List<StockQuantityDto> warehouseStock;

        try
        {
            batches = await sapClient.GetAllBatchNumbersInWarehouseAsync(warehouseCode, cancellationToken);
            warehouseStock = await sapClient.GetStockQuantitiesInWarehouseAsync(warehouseCode, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read SAP stock to refresh {WarehouseCode}", warehouseCode);
            return Error.Failure(
                "Stock.SapUnavailable",
                $"SAP could not be read for {warehouseCode}, so nothing was changed. Try again shortly.");
        }

        if (warehouseStock.Count == 0)
        {
            return Error.Failure(
                "Stock.SapReturnedNothing",
                $"SAP returned no stock at all for {warehouseCode}, which is a failed read rather than an "
                + "empty warehouse. Nothing was changed.");
        }

        var sapByItem = warehouseStock
            .Where(stock => !string.IsNullOrWhiteSpace(stock.ItemCode))
            .GroupBy(stock => stock.ItemCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var batchesByItem = batches
            .Where(batch => !string.IsNullOrWhiteSpace(batch.ItemCode))
            .GroupBy(batch => batch.ItemCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var ledgerByItem = (await db.DailyStockSnapshotItems
                .Where(row => row.Snapshot.SnapshotDate == ledgerDay && row.WarehouseCode == warehouseCode)
                .GroupBy(row => row.ItemCode)
                .Select(group => new { ItemCode = group.Key, Available = group.Sum(row => row.AvailableQuantity) })
                .ToListAsync(cancellationToken))
            .ToDictionary(total => total.ItemCode, total => total.Available, StringComparer.OrdinalIgnoreCase);

        var checkedCount = 0;
        var targets = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var (itemCode, ledgerQuantity) in ledgerByItem)
        {
            if (!sapByItem.TryGetValue(itemCode, out var stock))
            {
                // SAP does not carry the item in this warehouse at all. The job leaves these to the
                // morning fetch, for the same reason: it is what a decommissioned item looks like.
                continue;
            }

            checkedCount++;

            var target = Math.Max(0m, stock.Issuable - outstanding.GetValueOrDefault(itemCode));
            if (Math.Abs(target - ledgerQuantity) > Tolerance)
            {
                targets[itemCode] = target;
            }
        }

        var corrected = 0;

        if (targets.Count > 0)
        {
            corrected = await StockLedgerDivergenceJob.ApplyTargetsAsync(
                db, sapClient, ledgerDay, warehouseCode, targets, logger, cancellationToken, batchesByItem);
        }

        var added = await StockLedgerDivergenceJob.AddArrivalsAsync(
            db,
            snapshotId.Value,
            warehouseCode,
            ledgerByItem.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            batches,
            warehouseStock,
            outstanding,
            logger,
            cancellationToken);

        logger.LogInformation(
            "Stock for {WarehouseCode} refreshed from SAP by hand: {Checked} item(s) checked, "
            + "{Corrected} corrected, {Added} added",
            warehouseCode, checkedCount, corrected, added);

        return new RefreshWarehouseStockResult(
            warehouseCode, ledgerDay, checkedCount, corrected, added, DateTime.UtcNow);
    }
}
