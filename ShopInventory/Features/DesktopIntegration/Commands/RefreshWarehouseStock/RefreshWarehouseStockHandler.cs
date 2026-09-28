using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.FetchDailyStock;
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
/// <para><b>Not the morning fetch again.</b> Rebuilding a finished snapshot from SAP would hand back every
/// unit the tills have sold since 07:00 that has not reached SAP yet. The target here is SAP's
/// issuable quantity less those sales, as it is for the job.</para>
///
/// <para><b>Nothing is recorded about the change.</b> No movement, adjustment or divergence row: the
/// rows are moved and the change is logged. That is deliberate — a receipt is SAP's document, and
/// this is only catching the ledger up with it.</para>
///
/// <para><b>Vans are brought up to date by transfer, never by SAP's figure.</b> A van's row is its
/// morning load plus the transfers since, and its sales come off only at end of day. SAP's book figure
/// already has the day's online invoices taken off, so copying it would take those sales twice and
/// destroy the van reconciliation. What a van misses after 07:00 is a load transfer, and the transfers
/// are TransferEventListener's job: a van refresh makes the listener poll SAP now, through its own
/// session, and deliver anything new to the transfer webhook, which moves the rows as it always does.
/// That is every warehouse in <see cref="DailyStockSettings.MonitoredWarehouses"/> outside
/// <see cref="DailyStockSettings.ReconcileWarehouses"/>. One check covers every van, not only the one
/// pressed.</para>
///
/// <para><b>Refuses rather than guesses.</b> A warehouse that is not monitored at all is refused. So
/// is a snapshot still being fetched, and a SAP read that comes back empty — OITW holds a row for
/// every item a warehouse has ever carried, so empty is a failed read, and acting on it would zero the
/// shop.</para>
///
/// <para><b>A failed or missing snapshot is fetched again, not refused.</b> There are no rows to
/// correct then, and the tills are selling from yesterday's snapshot meanwhile (see
/// <see cref="StockSnapshotInForce"/>). The fix is the morning fetch for this one warehouse, which is
/// what the startup catch-up and the Fetch stock button run: it takes off the sales SAP has not had
/// and those made off yesterday's rows while it read, so nothing sold comes back. On 2026-09-25 the
/// morning fetch failed for KEFSHOP and this button refused with "fetch today's stock first", leaving
/// the operator nothing to press that fixed only that shop.</para>
/// </remarks>
public sealed class RefreshWarehouseStockHandler(
    ApplicationDbContext db,
    ISAPServiceLayerClient sapClient,
    IStockLedger ledger,
    IOptions<DailyStockSettings> dailyStock,
    FetchDailyStockHandler fetch,
    ITransferEventListenerClient transferListener,
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
        var requested = command.WarehouseCode?.Trim();
        var warehouseCode = settings.ReconcileWarehouses.FirstOrDefault(code =>
            string.Equals(code, requested, StringComparison.OrdinalIgnoreCase));

        if (warehouseCode is null)
        {
            var van = settings.MonitoredWarehouses.FirstOrDefault(code =>
                string.Equals(code, requested, StringComparison.OrdinalIgnoreCase));

            if (van is null)
            {
                return Error.Conflict(
                    "Stock.RefreshNotAllowed",
                    $"{command.WarehouseCode} is not a warehouse this system keeps stock for "
                    + "(DailyStock:MonitoredWarehouses), so there is nothing to refresh.");
            }

            return await RefreshVanAsync(van, cancellationToken);
        }

        if (!settings.ReconcileLedgerAgainstSap)
        {
            return Error.Conflict(
                "Stock.RefreshDisabled",
                "Refreshing stock from SAP is switched off (DailyStock:ReconcileLedgerAgainstSap).");
        }

        var ledgerDay = ledger.CurrentLedgerDay;

        var snapshot = await db.DailyStockSnapshots
            .AsNoTracking()
            .Where(header => header.SnapshotDate == ledgerDay && header.WarehouseCode == warehouseCode)
            .Select(header => new { header.Id, header.Status })
            .FirstOrDefaultAsync(cancellationToken);

        if (snapshot is { Status: StockSnapshotStatus.Pending })
        {
            return FetchInProgress(warehouseCode, ledgerDay);
        }

        if (snapshot is not { Status: StockSnapshotStatus.Complete })
        {
            return await RebuildSnapshotAsync(warehouseCode, ledgerDay, snapshot is null, cancellationToken);
        }

        var snapshotId = snapshot.Id;

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
            snapshotId,
            ledgerDay,
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

    /// <summary>
    /// Fetches the warehouse's snapshot for <paramref name="ledgerDay"/> from SAP again, for a day whose
    /// fetch failed or never ran. See the remarks on this class.
    /// </summary>
    private async Task<ErrorOr<RefreshWarehouseStockResult>> RebuildSnapshotAsync(
        string warehouseCode,
        DateTime ledgerDay,
        bool neverFetched,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Refresh for {WarehouseCode}: the {Day:yyyy-MM-dd} snapshot {State}, fetching it from SAP again",
            warehouseCode, ledgerDay, neverFetched ? "was never fetched" : "failed");

        WarehouseSnapshotResult fetched;
        try
        {
            fetched = await fetch.FetchWarehouseStockAsync(ledgerDay, warehouseCode, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The fetch has already marked the snapshot Failed and recorded this message against it.
            logger.LogWarning(ex, "Refetching the snapshot for {WarehouseCode} failed", warehouseCode);
            return Error.Failure(
                "Stock.SnapshotFetchFailed",
                $"{warehouseCode}'s stock could not be fetched from SAP: {ex.Message}");
        }

        if (fetched.Status == "AlreadyRunning")
        {
            return FetchInProgress(warehouseCode, ledgerDay);
        }

        logger.LogInformation(
            "Stock for {WarehouseCode} fetched from SAP again by hand: {Count} row(s), {Status}",
            warehouseCode, fetched.ItemCount, fetched.Status);

        return new RefreshWarehouseStockResult(
            warehouseCode, ledgerDay, 0, 0, 0, DateTime.UtcNow,
            SnapshotRefetched: true, RowsFetched: fetched.ItemCount);
    }

    /// <summary>
    /// Brings a van's rows up to date with the transfers SAP holds for it, by having
    /// TransferEventListener poll now. See the remarks on this class.
    /// </summary>
    /// <remarks>
    /// The rows are moved by the transfer webhook while the listener's check runs, not here, so the
    /// counts are read from the adjustments journalled against the van before and after. The check
    /// replays lines the listener was still holding too, which is what a van that stopped moving
    /// mid-morning usually needs.
    /// </remarks>
    private async Task<ErrorOr<RefreshWarehouseStockResult>> RefreshVanAsync(
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        var ledgerDay = ledger.CurrentLedgerDay;

        var status = await db.DailyStockSnapshots
            .AsNoTracking()
            .Where(header => header.SnapshotDate == ledgerDay && header.WarehouseCode == warehouseCode)
            .Select(header => (StockSnapshotStatus?)header.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (status is StockSnapshotStatus.Pending)
        {
            return FetchInProgress(warehouseCode, ledgerDay);
        }

        // No morning load to put transfers on. The fetch reads SAP's figure, which already has them.
        if (status is not StockSnapshotStatus.Complete)
        {
            return await RebuildSnapshotAsync(warehouseCode, ledgerDay, status is null, cancellationToken);
        }

        if (!transferListener.IsEnabled)
        {
            return Errors.DesktopIntegration.TransferListenerDisabled();
        }

        var before = await TransferLinesAsync(warehouseCode, ledgerDay, cancellationToken);

        TransferListenerCheckResultDto check;
        try
        {
            check = await transferListener.TriggerCheckAsync(cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's timeout, not the caller's. The listener's cycle runs about twenty seconds
            // and carries on after this side stops waiting, so what it finds still reaches the van.
            logger.LogWarning(
                ex, "Refresh for {WarehouseCode}: TransferEventListener's check outlasted the client timeout",
                warehouseCode);
            return Error.Failure(
                "Stock.TransferCheckTimedOut",
                $"The transfer listener is still checking SAP for {warehouseCode}'s transfers. Anything it "
                + "finds is applied when it finishes — load the stock again in a minute.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Refresh for {WarehouseCode}: TransferEventListener's check failed against {BaseUrl}",
                warehouseCode, transferListener.BaseUrl);
            return Errors.DesktopIntegration.TransferListenerUnreachable(transferListener.BaseUrl, ex.Message);
        }

        var after = await TransferLinesAsync(warehouseCode, ledgerDay, cancellationToken);

        logger.LogInformation(
            "Stock for {WarehouseCode} refreshed through TransferEventListener: {Applied} transfer line(s) "
            + "applied now, {Today} today; the check found {Detected} new line(s) across all warehouses "
            + "and {Pending} still wait",
            warehouseCode, after - before, after, check.MonitoredEventsDetected, check.PendingNotifications);

        return new RefreshWarehouseStockResult(
            warehouseCode, ledgerDay, 0, 0, 0, DateTime.UtcNow,
            ViaTransferListener: true,
            TransfersApplied: after - before,
            TransfersToday: after,
            TransfersPending: check.PendingNotifications);
    }

    /// <summary>Transfer lines the ledger has applied to the warehouse on <paramref name="ledgerDay"/>.</summary>
    private Task<int> TransferLinesAsync(string warehouseCode, DateTime ledgerDay, CancellationToken cancellationToken)
        => db.StockTransferAdjustments
            .AsNoTracking()
            .CountAsync(
                adjustment => adjustment.SnapshotDate == ledgerDay && adjustment.WarehouseCode == warehouseCode,
                cancellationToken);

    private static Error FetchInProgress(string warehouseCode, DateTime ledgerDay) =>
        Error.Conflict(
            "Stock.FetchInProgress",
            $"{warehouseCode}'s stock for {ledgerDay:dd MMM yyyy} is being fetched from SAP now. "
            + "Wait for it to finish, then refresh.");
}
