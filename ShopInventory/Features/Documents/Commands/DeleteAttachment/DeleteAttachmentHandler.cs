using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.DeleteAttachment;

public sealed class DeleteAttachmentHandler(
    IDocumentService documentService,
    ILogger<DeleteAttachmentHandler> logger
) : IRequestHandler<DeleteAttachmentCommand, ErrorOr<bool>>
{
    public async Task<ErrorOr<bool>> Handle(
        DeleteAttachmentCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await documentService.DeleteAttachmentAsync(command.Id, cancellationToken);
            if (!result)
            {
                return Errors.Document.AttachmentNotFound(command.Id);
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting attachment {AttachmentId}", command.Id);
            return Errors.Document.GenerationFailed(ex.Message);
        }
    }
}
