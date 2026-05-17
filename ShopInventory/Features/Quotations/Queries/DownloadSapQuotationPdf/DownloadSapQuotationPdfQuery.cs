using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Quotations.Queries.DownloadSapQuotationPdf;

public sealed record DownloadSapQuotationPdfQuery(int DocEntry) : IRequest<ErrorOr<(byte[] PdfBytes, string FileName)>>;