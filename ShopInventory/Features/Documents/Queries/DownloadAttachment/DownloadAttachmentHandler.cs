using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Features.Documents;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Queries.DownloadAttachment;

public sealed class DownloadAttachmentHandler(
    DocumentAttachmentAccessService attachmentAccessService,
    IDocumentService documentService,
    ILogger<DownloadAttachmentHandler> logger
) : IRequestHandler<DownloadAttachmentQuery, ErrorOr<AttachmentDownloadResult>>
{
    public async Task<ErrorOr<AttachmentDownloadResult>> Handle(
        DownloadAttachmentQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var accessResult = await attachmentAccessService.AuthorizeAttachmentAccessAsync(
                query.Id,
                false,
                cancellationToken);

            if (accessResult.IsError)
            {
                return accessResult.Errors;
            }

            var attachment = accessResult.Value;
            var (stream, fileName, mimeType) = await documentService.DownloadAttachmentAsync(query.Id, cancellationToken);

            if (stream == null)
            {
                return Errors.Document.AttachmentNotFound(query.Id);
            }

            return new AttachmentDownloadResult(
                stream,
                fileName ?? attachment.FileName,
                mimeType ?? attachment.MimeType ?? "application/octet-stream");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error downloading attachment {AttachmentId}", query.Id);
            return Errors.Document.GenerationFailed(ex.Message);
        }
    }
}
