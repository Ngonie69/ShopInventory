namespace ShopInventory.Features.CustomerDocuments.Documents;

/// <summary>
/// What is known about a SAP invoice, to check a fiscal receipt against before it is printed on the
/// copy a customer receives.
/// </summary>
/// <param name="DocNum">The invoice's number.</param>
/// <param name="CardCode">The customer it was raised to.</param>
/// <param name="DocTotal">SAP's total in the local currency.</param>
/// <param name="DocTotalFc">SAP's total in the document's currency, when that is a foreign one.</param>
/// <param name="DocDate">The invoice's date — a CAT calendar day.</param>
/// <param name="SaleReference">The sale reference SAP holds on the invoice (<c>U_Van_saleorder</c>).</param>
/// <param name="QueuedAtUtc">When the document was queued; the device is asked only after a while.</param>
public sealed record FiscalLinkQuery(
    int DocNum,
    string? CardCode,
    decimal? DocTotal,
    decimal? DocTotalFc,
    DateTime? DocDate,
    string? SaleReference,
    DateTime QueuedAtUtc);
