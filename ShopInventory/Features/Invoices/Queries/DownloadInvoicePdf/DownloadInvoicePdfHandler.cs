using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Invoices.Queries.DownloadInvoicePdf;

/// <summary>
/// The invoice PDF a member of staff downloads or prints.
/// </summary>
/// <remarks>
/// The rendering lives in <see cref="IInvoicePdfComposer"/>, shared with the copy sent to a customer
/// on WhatsApp, so what a customer receives is the document the office sees.
/// </remarks>
public sealed class DownloadInvoicePdfHandler(
    IInvoicePdfComposer composer
) : IRequestHandler<DownloadInvoicePdfQuery, ErrorOr<(byte[] PdfBytes, string FileName)>>
{
    public async Task<ErrorOr<(byte[] PdfBytes, string FileName)>> Handle(
        DownloadInvoicePdfQuery request,
        CancellationToken cancellationToken)
    {
        var composed = await composer.ComposeAsync(
            request.DocEntry,
            request.FiscalQrCode,
            verifiedReceipt: null,
            cancellationToken);

        if (composed.IsError)
            return composed.Errors;

        var fileName = $"Invoice_{composed.Value.Invoice.DocNum}_{DateTime.Now:yyyyMMdd}.pdf";
        return (composed.Value.PdfBytes, fileName);
    }
}
