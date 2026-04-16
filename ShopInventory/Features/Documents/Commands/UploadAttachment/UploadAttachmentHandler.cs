using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.UploadAttachment;

public sealed class UploadAttachmentHandler(
    IDocumentService documentService,
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

            return attachment;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error uploading attachment");
            return Errors.Document.UploadFailed(ex.Message);
        }
    }
}
