using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Invoices.Queries.DownloadInvoiceAttachment;

public sealed record DownloadInvoiceAttachmentQuery(int DocEntry, int AttachmentId) : IRequest<ErrorOr<(Stream? Stream, string? FileName, string? MimeType)>>;
