using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Features.Documents;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.DeleteAttachment;

public sealed class DeleteAttachmentHandler(
    DocumentAttachmentAccessService attachmentAccessService,
    IDocumentService documentService,
    IAuditService auditService,
    ILogger<DeleteAttachmentHandler> logger
) : IRequestHandler<DeleteAttachmentCommand, ErrorOr<bool>>
{
    public async Task<ErrorOr<bool>> Handle(
        DeleteAttachmentCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var accessResult = await attachmentAccessService.AuthorizeAttachmentAccessAsync(
                command.Id,
                true,
                cancellationToken);

            if (accessResult.IsError)
            {
                return accessResult.Errors;
            }

            var attachment = accessResult.Value;
            var result = await documentService.DeleteAttachmentAsync(command.Id, cancellationToken);
            if (!result)
            {
                return Errors.Document.AttachmentNotFound(command.Id);
            }

            try
            {
                await auditService.LogAsync(
                    AuditActions.DeleteDocumentAttachment,
                    attachment.EntityType,
                    attachment.Id.ToString(),
                    $"Attachment '{attachment.FileName}' deleted from {attachment.EntityType}/{attachment.EntityId}",
                    true);
            }
            catch
            {
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
