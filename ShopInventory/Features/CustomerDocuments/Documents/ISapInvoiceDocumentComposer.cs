using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CustomerDocuments.Documents;

/// <summary>
/// Prepares a SAP invoice for sending to a customer: checks it should still go, finds and verifies
/// its fiscal receipt, and renders the PDF with that receipt on it.
/// </summary>
public interface ISapInvoiceDocumentComposer
{
    Task<DocumentComposition> ComposeAsync(
        CustomerDocumentDeliveryEntity delivery,
        DateTime nowUtc,
        CancellationToken cancellationToken);
}
