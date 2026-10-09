namespace ShopInventory.Features.CustomerDocuments.Commands.ScanNewInvoicesForDelivery;

/// <summary>What one scan pass did.</summary>
/// <param name="Outcome">In a few words, for the log.</param>
/// <param name="Read">Invoice headers read from SAP.</param>
/// <param name="Queued">Automatic sends queued.</param>
/// <param name="Skipped">Invoices of an opted-in customer recorded as deliberately not sent.</param>
/// <param name="LastDocEntry">The watermark after the pass; null when the pass did not get that far.</param>
public sealed record ScanNewInvoicesForDeliveryResult(
    string Outcome,
    int Read,
    int Queued,
    int Skipped,
    int? LastDocEntry);
