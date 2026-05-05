using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Features.Documents;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Queries.DownloadInvoiceAttachment;

public sealed class DownloadInvoiceAttachmentHandler(
    DocumentAttachmentAccessService attachmentAccessService,
    IDocumentService documentService
) : IRequestHandler<DownloadInvoiceAttachmentQuery, ErrorOr<(Stream? Stream, string? FileName, string? MimeType)>>
{
    public async Task<ErrorOr<(Stream? Stream, string? FileName, string? MimeType)>> Handle(
        DownloadInvoiceAttachmentQuery request,
        CancellationToken cancellationToken)
    {
        var accessResult = await attachmentAccessService.AuthorizeAttachmentAccessAsync(
            request.AttachmentId,
            false,
            cancellationToken);

        if (accessResult.IsError)
        {
            return accessResult.Errors;
        }

        var attachment = accessResult.Value;
        if (!string.Equals(attachment.EntityType, "Invoice", StringComparison.OrdinalIgnoreCase) ||
            attachment.EntityId != request.DocEntry)
        {
            return Errors.Invoice.NotFound(request.AttachmentId);
        }

        var (stream, fileName, mimeType) = await documentService.DownloadAttachmentAsync(request.AttachmentId, cancellationToken);
        if (stream == null)
        {
            return Errors.Invoice.NotFound(request.AttachmentId);
        }

        return (stream, fileName, mimeType);
    }
}
