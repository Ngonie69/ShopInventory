using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Common.Stock;

/// <summary>
/// One SAP stock transfer's effect on one warehouse's ledger: one item, one direction, summed over
/// the document's lines. The grain the transfer webhook deduplicates at.
/// </summary>
/// <remarks><c>Quantity</c> is unsigned, in the item's inventory unit.</remarks>
public sealed record BookedTransfer(
    int DocEntry,
    int DocNum,
    string ItemCode,
    string Direction,
    decimal Quantity,
    string SourceWarehouse,
    string DestinationWarehouse)
{
    /// <summary>As the balance moves: negative for stock leaving the warehouse.</summary>
    public decimal Signed => Direction == UndeliveredTransfers.Out ? -Quantity : Quantity;
}

/// <summary>
/// Stock transfers SAP has booked that TransferEventListener has not yet delivered to the ledger, and
/// the means of recording them as delivered.
/// </summary>
/// <remarks>
/// <para><b>The race this closes.</b> Anything that moves the ledger to SAP's figure takes every
/// transfer SAP has booked along with it. The listener polls SAP every two minutes and posts each
/// transfer to the webhook, which moves the rows again. So a correction taken between SAP booking a
/// transfer and the listener delivering it counted the transfer twice. On 2026-09-28 at KEFGRC the
/// 09:10 pass drew VHU002 down 320 for transfer 129199 to VAN006, the webhook took the same 320 off
/// at 09:12, and the depot's till sat 320 short until the 10:13 pass put them back. An inbound
/// transfer caught the same way overstates the ledger, which is the direction that oversells.</para>
///
/// <para><b>Why the correction records the transfer rather than netting it out.</b> The other way
/// round — aim the correction at SAP's figure plus the outbound transfers the ledger has not had yet —
/// leaves the ledger wrong on purpose until the webhook lands, and wrong for the rest of the day if
/// it never does: the listener abandons lines, and has gone days unable to deliver. Recording the
/// transfer as the correction's own puts the ledger right at once and hands the webhook's existing
/// duplicate check the answer, so the later delivery is skipped. The two writers then meet at one
/// unique key (<see cref="StockTransferAdjustmentEntity"/>'s), and whichever saves second fails
/// rather than taking the units again.</para>
///
/// <para><b>Read before SAP's stock figure, never after.</b> A transfer in this list was booked
/// before the stock read began, so the figure includes it. One booked between the two reads is in
/// the figure and not in the list, and the webhook takes it a second time, as it did before this
/// existed — a window of seconds rather than the listener's two minutes. Read the other way round,
/// a transfer booked in between would be recorded as delivered while missing from the figure, and
/// lost from the ledger outright.</para>
/// </remarks>
public static class UndeliveredTransfers
{
    /// <summary>The webhook's direction strings, which are part of the deduplication key.</summary>
    public const string Out = "OUT";

    public const string In = "IN";

    /// <summary>
    /// The transfers SAP has created since the start of <paramref name="ledgerDay"/>'s calendar date
    /// that move stock into or out of <paramref name="warehouseCode"/>.
    /// </summary>
    /// <remarks>
    /// By creation date, not document date: a transfer dated yesterday and keyed in this morning is
    /// in SAP's figure all the same. From midnight rather than from the 07:00 fetch, because SAP keeps
    /// no time on a transfer. One booked before the fetch is in the morning snapshot too, so
    /// recording it as delivered is also right — it stops a late delivery taking it off a second
    /// time.
    ///
    /// <para>An empty list when SAP cannot be read. The caller then corrects as it always did, with
    /// nothing recorded as delivered: the race stays open for this pass rather than the correction
    /// being lost for the hour.</para>
    /// </remarks>
    public static async Task<IReadOnlyList<BookedTransfer>> ReadAsync(
        ISAPServiceLayerClient sapClient,
        string warehouseCode,
        DateTime ledgerDay,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        List<SapStockMovementDocument> documents;

        try
        {
            documents = await sapClient.GetStockTransfersCreatedAsync(
                [warehouseCode], ledgerDay.Date, ledgerDay.Date.AddDays(1), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Could not read SAP's stock transfers for {WarehouseCode}; this pass corrects without "
                + "recording any as delivered, so one the listener has not delivered yet may be taken twice",
                warehouseCode);
            return [];
        }

        return Movements(documents, warehouseCode);
    }

    /// <summary>
    /// Each document's movement of each item into or out of <paramref name="warehouseCode"/>.
    /// </summary>
    /// <remarks>
    /// A line's own warehouses win over the header's, the order the listener reads them in. Summed per
    /// document, item and direction because that is the webhook's key: it would apply the first line of
    /// an item and skip the rest as duplicates. A line moving stock from the warehouse to itself moves
    /// nothing and is left out.
    /// </remarks>
    internal static IReadOnlyList<BookedTransfer> Movements(
        IEnumerable<SapStockMovementDocument> documents,
        string warehouseCode)
    {
        var movements = new List<BookedTransfer>();

        foreach (var document in documents)
        {
            var lines = document.Lines
                .Where(line => !string.IsNullOrWhiteSpace(line.ItemCode))
                .Select(line => new
                {
                    ItemCode = line.ItemCode!.Trim(),
                    From = FirstNonBlank(line.FromWarehouseCode, document.FromWarehouse),
                    To = FirstNonBlank(line.WarehouseCode, document.ToWarehouse),
                    line.StockQuantity
                })
                .Where(line => !Is(line.From, line.To))
                .Select(line => new
                {
                    line.ItemCode,
                    line.From,
                    line.To,
                    line.StockQuantity,
                    Direction = Is(line.From, warehouseCode) ? Out : Is(line.To, warehouseCode) ? In : null
                })
                .Where(line => line.Direction is not null && line.StockQuantity > 0);

            movements.AddRange(lines
                .GroupBy(line => (Item: line.ItemCode.ToUpperInvariant(), line.Direction))
                .Select(group => new BookedTransfer(
                    document.DocEntry,
                    document.DocNum,
                    group.First().ItemCode,
                    group.Key.Direction!,
                    group.Sum(line => line.StockQuantity),
                    group.First().From,
                    group.First().To)));
        }

        return movements;
    }

    /// <summary>
    /// The transfers among <paramref name="booked"/> for <paramref name="itemCodes"/> that the ledger has
    /// no adjustment for yet, indexed by item.
    /// </summary>
    /// <remarks>
    /// Any adjustment for the document, item, warehouse and direction counts, whatever day it was
    /// recorded against: one delivered before this morning's fetch is in the snapshot already. Asked of
    /// the database at the moment of correcting, not when SAP was read, so a delivery that landed while
    /// SAP was being read is seen and left alone.
    /// </remarks>
    public static async Task<Dictionary<string, List<BookedTransfer>>> NotYetJournalledAsync(
        ApplicationDbContext db,
        string warehouseCode,
        DateTime ledgerDay,
        IReadOnlyList<BookedTransfer> booked,
        IEnumerable<string> itemCodes,
        CancellationToken cancellationToken)
    {
        var items = itemCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relevant = booked.Where(transfer => items.Contains(transfer.ItemCode)).ToList();

        if (relevant.Count == 0)
        {
            return new Dictionary<string, List<BookedTransfer>>(StringComparer.OrdinalIgnoreCase);
        }

        var docEntries = relevant.Select(transfer => (int?)transfer.DocEntry).Distinct().ToList();

        // Two days back is as early as a transfer created since midnight can have been recorded: the
        // ledger day before this one runs until this morning's fetch.
        var since = ledgerDay.Date.AddDays(-2);

        var journalled = (await db.StockTransferAdjustments
                .AsNoTracking()
                .Where(adjustment => adjustment.SnapshotDate >= since
                                  && adjustment.WarehouseCode == warehouseCode
                                  && docEntries.Contains(adjustment.TransferDocEntry))
                .Select(adjustment => new { adjustment.TransferDocEntry, adjustment.ItemCode, adjustment.Direction })
                .ToListAsync(cancellationToken))
            .Select(adjustment => Key(adjustment.TransferDocEntry!.Value, adjustment.ItemCode, adjustment.Direction))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return relevant
            .Where(transfer => !journalled.Contains(Key(transfer.DocEntry, transfer.ItemCode, transfer.Direction)))
            .GroupBy(transfer => transfer.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(transfer => transfer.DocEntry).ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Records <paramref name="transfers"/> as delivered to the ledger, exactly as the webhook records
    /// them, and returns the balance they account for.
    /// </summary>
    /// <remarks>
    /// Only the adjustment row and the journal entry: the caller has already moved the rows to a
    /// figure that includes these transfers, and journals whatever part of its movement they do not
    /// explain. The journal key is the webhook's, so the day's movements for a document read the same
    /// whichever of the two got there first. <c>balanceBefore</c> is the item's balance before the
    /// caller's movement.
    /// </remarks>
    public static decimal Record(
        ApplicationDbContext db,
        DateTime ledgerDay,
        string warehouseCode,
        IEnumerable<BookedTransfer> transfers,
        decimal balanceBefore)
    {
        var balance = balanceBefore;

        foreach (var transfer in transfers)
        {
            db.StockTransferAdjustments.Add(new StockTransferAdjustmentEntity
            {
                SnapshotDate = ledgerDay,
                ItemCode = transfer.ItemCode,
                WarehouseCode = warehouseCode,
                AdjustmentQuantity = transfer.Signed,
                Direction = transfer.Direction,
                TransferDocEntry = transfer.DocEntry,
                TransferDocNum = transfer.DocNum,
                SourceWarehouse = transfer.SourceWarehouse,
                DestinationWarehouse = transfer.DestinationWarehouse,
                DetectedAt = DateTime.UtcNow
            });

            balance += transfer.Signed;

            StockMovementJournal.Append(
                db,
                ledgerDay,
                StockMovementKinds.Transfer,
                $"transfer:{transfer.DocEntry}:{transfer.Direction}",
                transfer.ItemCode,
                warehouseCode,
                transfer.Signed,
                balance,
                $"transfer {transfer.DocNum} {transfer.Direction} {transfer.SourceWarehouse} to "
                + $"{transfer.DestinationWarehouse}, taken from SAP ahead of the listener");
        }

        return balance;
    }

    private static string Key(int docEntry, string itemCode, string direction) =>
        $"{docEntry}|{itemCode.Trim()}|{direction}";

    private static string FirstNonBlank(string? line, string? header) =>
        (string.IsNullOrWhiteSpace(line) ? header : line)?.Trim() ?? string.Empty;

    private static bool Is(string? code, string warehouseCode) =>
        string.Equals(code?.Trim(), warehouseCode.Trim(), StringComparison.OrdinalIgnoreCase);
}
