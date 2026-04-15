using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Invoices.Queries.DownloadInvoicePdf;

public sealed record DownloadInvoicePdfQuery(int DocEntry) : IRequest<ErrorOr<(byte[] PdfBytes, string FileName)>>;
