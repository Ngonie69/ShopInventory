using ErrorOr;
using MediatR;

namespace ShopInventory.Features.SalesOrders.Queries.DownloadSalesOrderPdf;

public sealed record DownloadSalesOrderPdfQuery(int Id, bool UseLocal = false) : IRequest<ErrorOr<(byte[] PdfBytes, string FileName)>>;