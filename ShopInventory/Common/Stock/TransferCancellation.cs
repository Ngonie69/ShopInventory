namespace ShopInventory.Common.Stock;

/// <summary>
/// Reads an SAP stock transfer line as the movement it makes, cancellations included.
/// </summary>
/// <remarks>
/// <para>Cancelling a transfer in SAP does not delete it: SAP books a cancellation document with the
/// original's From and To warehouses and every quantity negated. So a line of -575 from KEFBYC to
/// VAN010 is 575 going back from VAN010 to KEFBYC. On 2026-10-12 a van load keyed in twice (91023,
/// 91045) was cancelled by 91086, and on 2026-10-14 a van return (91443) by 91445. Both cancellations
/// were refused by the webhook as invalid, because it took a negative quantity for a malformed line;
/// the van's ledger kept the duplicate load for the rest of the day, and the transfer-listener health
/// check stayed Degraded until the listener restarted.</para>
///
/// <para>Both writers of the transfer ledger — the webhook and the correction that records SAP's
/// transfers ahead of it — read lines through this, so they meet at the same deduplication key
/// (document, item, warehouse, direction) and a cancellation is never taken twice.</para>
/// </remarks>
public static class TransferCancellation
{
    /// <summary>
    /// The line as a forward movement: unchanged when its quantity is positive, otherwise with its
    /// warehouses swapped and its quantity made positive. A zero quantity is returned as it came.
    /// </summary>
    public static (string From, string To, decimal Quantity) AsMovement(string from, string to, decimal quantity) =>
        quantity < 0 ? (to, from, -quantity) : (from, to, quantity);
}
