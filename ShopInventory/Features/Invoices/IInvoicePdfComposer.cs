using ErrorOr;

namespace ShopInventory.Features.Invoices;

/// <summary>
/// Reads a SAP invoice and renders it as the Fiscal Tax Invoice PDF.
/// </summary>
/// <remarks>
/// One composer for every reader of the PDF — the download a member of staff presses and the copy a
/// customer is sent on WhatsApp — so the two cannot drift into printing different documents.
/// </remarks>
public interface IInvoicePdfComposer
{
    /// <summary>
    /// Renders invoice <paramref name="docEntry"/>.
    /// </summary>
    /// <param name="docEntry">The SAP invoice.</param>
    /// <param name="requestedQrCode">A QR the caller already holds; the download's query parameter.</param>
    /// <param name="verifiedReceipt">
    /// A receipt the caller has verified belongs to this invoice. When given it is printed as it is —
    /// QR, verification code, day and device together — and nothing keyed on the DocNum is consulted.
    /// </param>
    /// <param name="cancellationToken">Cancels the SAP reads.</param>
    /// <param name="buyer">
    /// Who to print as the customer instead of the SAP card — a van sale's shop. Null prints the card,
    /// as every download does.
    /// </param>
    Task<ErrorOr<ComposedInvoicePdf>> ComposeAsync(
        int docEntry,
        string? requestedQrCode,
        InvoicePdfReceipt? verifiedReceipt,
        CancellationToken cancellationToken,
        InvoicePdfBuyer? buyer = null);
}
