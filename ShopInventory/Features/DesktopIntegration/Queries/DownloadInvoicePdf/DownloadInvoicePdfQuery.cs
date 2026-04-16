using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.DownloadInvoicePdf;

public sealed record InvoicePdfResult(
    byte[] PdfBytes,
    string FileName
);

public sealed record DownloadInvoicePdfQuery(
    int DocEntry
) : IRequest<ErrorOr<InvoicePdfResult>>;
