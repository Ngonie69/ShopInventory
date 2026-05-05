using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.Documents;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.UploadAttachment;

public sealed class UploadAttachmentHandler(
    DocumentAttachmentAccessService attachmentAccessService,
    IDocumentService documentService,
    IAuditService auditService,
    ILogger<UploadAttachmentHandler> logger
) : IRequestHandler<UploadAttachmentCommand, ErrorOr<DocumentAttachmentDto>>
{
    public async Task<ErrorOr<DocumentAttachmentDto>> Handle(
        UploadAttachmentCommand command,
        CancellationToken cancellationToken)
    {
        if (command.FileBytes.Length == 0)
        {
            return Errors.Document.UploadFailed("No file uploaded");
        }

        try
        {
            var accessResult = await attachmentAccessService.AuthorizeEntityAccessAsync(
                command.EntityType,
                command.EntityId,
                true,
                cancellationToken);

            if (accessResult.IsError)
            {
                return accessResult.Errors;
            }

            var request = new UploadAttachmentRequest
            {
                EntityType = command.EntityType,
                EntityId = command.EntityId,
                Description = command.Description,
                IncludeInEmail = command.IncludeInEmail
            };

            using var stream = new MemoryStream(command.FileBytes);
            var attachment = await documentService.UploadAttachmentAsync(
                request, stream, command.FileName, command.ContentType, command.UserId, cancellationToken);

            try
            {
                await auditService.LogAsync(
                    AuditActions.UploadDocumentAttachment,
                    attachment.EntityType,
                    attachment.Id.ToString(),
                    $"Attachment '{attachment.FileName}' uploaded for {attachment.EntityType}/{attachment.EntityId}",
                    true);
            }
            catch
            {
            }

            return attachment;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error uploading attachment");
            return Errors.Document.UploadFailed(ex.Message);
        }
    }
}
