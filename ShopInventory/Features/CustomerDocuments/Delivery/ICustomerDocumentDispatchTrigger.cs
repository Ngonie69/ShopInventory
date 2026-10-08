namespace ShopInventory.Features.CustomerDocuments.Delivery;

/// <summary>
/// Wakes the delivery job now, so a document someone just asked to send does not wait for the next pass.
/// </summary>
public interface ICustomerDocumentDispatchTrigger
{
    /// <summary>Asks the cluster to run the delivery job. Never throws: the scheduled pass sends it anyway.</summary>
    Task TriggerAsync(CancellationToken cancellationToken);
}
