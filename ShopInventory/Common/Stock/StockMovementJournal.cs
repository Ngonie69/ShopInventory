using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Stock;

/// <summary>
/// The one way a movement gets into the ledger's journal.
/// </summary>
/// <remarks>
/// It exists to be the only one. The journal is worth reading exactly as far as it is complete, and
/// the invariant that makes completeness checkable — opening quantity plus everything journalled is
/// the figure the guard reads — is only enforceable if every writer appends through the same code.
/// A second hand-rolled <c>StockMovements.Add</c> elsewhere would not fail any test; it would
/// quietly weaken every reading taken from the table.
///
/// <para>Deliberately not a service: it adds to the caller's change tracker and never saves. The
/// balance and the movement that explains it have to reach the database in one transaction or
/// neither does, and that transaction belongs to whoever is moving the stock.</para>
/// </remarks>
public static class StockMovementJournal
{
    private const int ReferenceLength = 200;

    /// <param name="context">The caller's context. The row is added, not saved.</param>
    /// <param name="ledgerDay">The snapshot day being moved, resolved by <see cref="StockLedgerDay"/>.</param>
    /// <param name="kind">One of <see cref="StockMovementKinds"/>.</param>
    /// <param name="documentKey">
    /// The document's identity, or null when it has none. Null disables deduplication for this
    /// movement — which is the honest answer when the caller cannot name the document, and far
    /// safer than a key that might repeat across two of them.
    /// </param>
    /// <param name="itemCode">The item that moved.</param>
    /// <param name="warehouseCode">Where it moved.</param>
    /// <param name="quantity">
    /// Signed as the balance moved: negative where units left. What actually moved, not what was
    /// asked for — a claim that ran the rows out must journal the units it really got, or the
    /// invariant stops holding for that item.
    /// </param>
    /// <param name="balanceAfter">
    /// What the item held in that warehouse afterwards, across every row including any sitting at
    /// zero. A total that silently omits empty rows is the reading that makes a later comparison
    /// look like a divergence.
    /// </param>
    /// <param name="reference">How the movement reads to a person. Truncated rather than rejected.</param>
    public static void Append(
        ApplicationDbContext context,
        DateTime ledgerDay,
        string kind,
        string? documentKey,
        string itemCode,
        string warehouseCode,
        decimal quantity,
        decimal balanceAfter,
        string reference)
    {
        // A movement of nothing is not a fact worth a row. It also cannot be distinguished from the
        // absence of one when the column is summed, so recording it would only add noise.
        if (quantity == 0)
        {
            return;
        }

        context.StockMovements.Add(new StockMovementEntity
        {
            LedgerDay = ledgerDay,
            Kind = kind,
            DocumentKey = documentKey,
            ItemCode = itemCode,
            WarehouseCode = warehouseCode,
            Quantity = quantity,
            BalanceAfter = balanceAfter,
            Reference = reference.Length <= ReferenceLength
                ? reference
                : reference[..ReferenceLength]
        });
    }
}
