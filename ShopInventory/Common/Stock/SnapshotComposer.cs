using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Common.Stock;

/// <param name="ItemCode">The item the row is for.</param>
/// <param name="ItemDescription">Its name, as SAP gave it.</param>
/// <param name="BatchNumber">The batch, or null for an item SAP does not manage by batch.</param>
/// <param name="OriginalQuantity">What the morning read said, before commitments were taken off.</param>
/// <param name="AvailableQuantity">What may actually be promised: the row's share of issuable stock.</param>
/// <param name="ExpiryDate">For FEFO ordering.</param>
public sealed record ComposedSnapshotRow(
    string ItemCode,
    string? ItemDescription,
    string? BatchNumber,
    decimal OriginalQuantity,
    decimal AvailableQuantity,
    DateTime? ExpiryDate);

/// <param name="Rows">The snapshot rows to store.</param>
/// <param name="Notes">Things a person should know about this warehouse's figures.</param>
public sealed record ComposedSnapshot(
    IReadOnlyList<ComposedSnapshotRow> Rows,
    IReadOnlyList<string> Notes);

/// <summary>
/// Turns SAP's two views of a warehouse into the day's sellable position.
/// </summary>
/// <remarks>
/// <para><b>Why two reads.</b> The snapshot used to be built from batch rows alone, which left two
/// holes. An item SAP does not manage by batch has no batch row at all, so it got no snapshot row,
/// summed to zero, and could never be sold from a till — silently, for as long as it existed. And a
/// batch quantity is gross: it counts stock already committed to other documents, so the snapshot
/// started every morning more optimistic than the live figure the web invoice path was checking
/// against. The two ledgers disagreed from the moment they were created.</para>
///
/// <para><b>How commitments come off a batch-managed item.</b> SAP commits at item level and holds
/// batches separately, so there is no per-batch commitment to subtract. They come off the
/// earliest-expiring batches first, which is the same order the stock will actually be issued in —
/// so what is left is the batches a later sale would really be given.</para>
///
/// <para>Pure, and separate from the fetch handler, because these are the rules worth testing and
/// SAP is not.</para>
/// </remarks>
public static class SnapshotComposer
{
    public static ComposedSnapshot Compose(
        IEnumerable<BatchNumber>? batches,
        IEnumerable<StockQuantityDto>? warehouseStock)
    {
        var rows = new List<ComposedSnapshotRow>();
        var notes = new List<string>();

        var batchesByItem = (batches ?? [])
            .Where(batch => !string.IsNullOrWhiteSpace(batch.ItemCode))
            .GroupBy(batch => batch.ItemCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var stockByItem = (warehouseStock ?? [])
            .Where(stock => !string.IsNullOrWhiteSpace(stock.ItemCode))
            .GroupBy(stock => stock.ItemCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var (itemCode, stock) in stockByItem.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            var issuable = stock.Issuable;

            if (batchesByItem.TryGetValue(itemCode, out var itemBatches))
            {
                rows.AddRange(ComposeBatchManaged(itemCode, stock, itemBatches, issuable, notes));
                continue;
            }

            // No batch rows: either SAP does not manage the item by batch, or it holds none here.
            // Either way one row carries the whole position, and without it the item was unsellable.
            if (issuable <= 0)
            {
                continue;
            }

            rows.Add(new ComposedSnapshotRow(
                itemCode,
                stock.ItemName,
                BatchNumber: null,
                OriginalQuantity: stock.InStock,
                AvailableQuantity: issuable,
                ExpiryDate: null));
        }

        // Batches for an item the warehouse read did not mention. Reported rather than stored: with
        // no issuable figure there is nothing to say how much of it may be promised, and guessing
        // the batch total is how the gross-quantity problem got here in the first place.
        foreach (var itemCode in batchesByItem.Keys
                     .Where(code => !stockByItem.ContainsKey(code))
                     .OrderBy(code => code, StringComparer.OrdinalIgnoreCase))
        {
            notes.Add(
                $"{itemCode}: SAP holds batches here but the warehouse stock read does not list the item, "
                + "so it has no snapshot row and cannot be sold from a till today.");
        }

        return new ComposedSnapshot(rows, notes);
    }

    private static IEnumerable<ComposedSnapshotRow> ComposeBatchManaged(
        string itemCode,
        StockQuantityDto stock,
        List<BatchNumber> itemBatches,
        decimal issuable,
        List<string> notes)
    {
        var ordered = itemBatches
            .Select(batch => (Batch: batch, Expiry: ParseExpiry(batch.ExpiryDate)))
            .OrderBy(entry => entry.Expiry ?? DateTime.MaxValue)
            .ThenBy(entry => entry.Batch.BatchNum, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var batchTotal = ordered.Sum(entry => entry.Batch.Quantity);

        if (batchTotal > 0 && issuable > batchTotal)
        {
            // More issuable than the batches account for. SAP will still only release what the
            // batches hold, so the batches are the binding figure — but the gap is worth knowing
            // about, because it usually means a receipt was posted without its batch allocation.
            notes.Add(
                $"{itemCode}: warehouse stock shows {issuable:N4} issuable but its batches hold only "
                + $"{batchTotal:N4}. The snapshot follows the batches.");
        }

        // Commitments come off the FRONT of the FEFO queue, not the back. SAP issues
        // earliest-expiring first, so an existing document will be given the soonest batches — they
        // are precisely what a new sale cannot have. Filling availability from the front instead
        // gets this exactly backwards and hands a new sale the stock already promised elsewhere.
        var committed = Math.Max(0m, batchTotal - Math.Min(issuable, batchTotal));

        foreach (var (batch, expiry) in ordered)
        {
            var quantity = batch.Quantity;
            if (quantity <= 0)
            {
                continue;
            }

            var spokenFor = Math.Min(quantity, committed);
            committed -= spokenFor;
            var available = quantity - spokenFor;

            if (available <= 0)
            {
                // Fully spoken for. The row is still written: it says the batch is here and holds
                // nothing sellable, which is what a stock enquiry should show.
                yield return new ComposedSnapshotRow(
                    itemCode, stock.ItemName ?? batch.ItemName, batch.BatchNum, quantity, 0m, expiry);
                continue;
            }

            yield return new ComposedSnapshotRow(
                itemCode, stock.ItemName ?? batch.ItemName, batch.BatchNum, quantity, available, expiry);
        }
    }

    private static DateTime? ParseExpiry(string? expiryDate) =>
        DateTime.TryParse(expiryDate, out var parsed) ? parsed : null;
}
