namespace ShopInventory.Features.CustomerDocuments.Commands.DispatchCustomerDocumentDeliveries;

/// <summary>What one delivery pass did.</summary>
/// <param name="Outcome">Why the pass stopped where it did, in words for the log.</param>
/// <param name="Sent">Documents WhatsApp accepted, confirmed or not.</param>
/// <param name="Deferred">Documents put back to wait — for a receipt, for SAP, for tomorrow's cap.</param>
/// <param name="Closed">Documents closed without sending: cancelled, failed, held, not on WhatsApp.</param>
/// <param name="Uncertain">Sends whose answer was lost, left for the next pass to settle.</param>
/// <param name="Reconciled">Earlier uncertain sends settled from the gateway's log.</param>
public sealed record DispatchCustomerDocumentDeliveriesResult(
    string Outcome,
    int Sent = 0,
    int Deferred = 0,
    int Closed = 0,
    int Uncertain = 0,
    int Reconciled = 0)
{
    public static DispatchCustomerDocumentDeliveriesResult Idle(string reason) => new(reason);
}
