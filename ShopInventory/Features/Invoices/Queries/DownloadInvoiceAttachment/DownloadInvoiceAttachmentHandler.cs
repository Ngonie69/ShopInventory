using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Queries.DownloadInvoiceAttachment;

public sealed class DownloadInvoiceAttachmentHandler(
    IDocumentService documentService
) : IRequestHandler<DownloadInvoiceAttachmentQuery, ErrorOr<(Stream? Stream, string? FileName, string? MimeType)>>
{
    public async Task<ErrorOr<(Stream? Stream, string? FileName, string? MimeType)>> Handle(
        DownloadInvoiceAttachmentQuery request,
        CancellationToken cancellationToken)
    {
        var attachments = await documentService.GetAttachmentsAsync("Invoice", request.DocEntry, cancellationToken);
        if (!attachments.Attachments.Any(a => a.Id == request.AttachmentId))
            return Errors.Invoice.NotFound(request.AttachmentId);

        var (stream, fileName, mimeType) = await documentService.DownloadAttachmentAsync(request.AttachmentId, cancellationToken);
        if (stream == null)
            return Errors.Invoice.NotFound(request.AttachmentId);

        return (stream, fileName, mimeType);
    }
}
