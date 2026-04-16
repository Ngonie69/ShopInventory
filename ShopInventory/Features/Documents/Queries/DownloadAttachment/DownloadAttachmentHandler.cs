using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Queries.DownloadAttachment;

public sealed class DownloadAttachmentHandler(
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
            var (stream, fileName, mimeType) = await documentService.DownloadAttachmentAsync(query.Id, cancellationToken);

            if (stream == null)
            {
                return Errors.Document.AttachmentNotFound(query.Id);
            }

            return new AttachmentDownloadResult(
                stream,
                fileName ?? "attachment",
                mimeType ?? "application/octet-stream");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error downloading attachment {AttachmentId}", query.Id);
            return Errors.Document.GenerationFailed(ex.Message);
        }
    }
}
