using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Quotations.Queries.DownloadQuotationPdf;

public sealed record DownloadQuotationPdfQuery(int Id) : IRequest<ErrorOr<(byte[] PdfBytes, string FileName)>>;