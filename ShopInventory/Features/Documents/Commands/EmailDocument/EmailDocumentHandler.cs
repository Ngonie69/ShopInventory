using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.Documents;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.EmailDocument;

public sealed class EmailDocumentHandler(
    DocumentAttachmentAccessService accessService,
    IDocumentService documentService,
    ILogger<EmailDocumentHandler> logger
) : IRequestHandler<EmailDocumentCommand, ErrorOr<GenerateDocumentResponseDto>>
{
    public async Task<ErrorOr<GenerateDocumentResponseDto>> Handle(
        EmailDocumentCommand command,
        CancellationToken cancellationToken)
    {
        // Emailing sends the rendered document and the entity's attachments to an outside address, so the
        // caller must be able to read the entity and to act on it.
        foreach (var isWriteOperation in new[] { false, true })
        {
            var access = await accessService.AuthorizeEntityAccessAsync(
                command.Request.DocumentType, command.Request.EntityId, isWriteOperation, cancellationToken);
            if (access.IsError)
            {
                return access.Errors;
            }
        }

        try
        {
            var result = await documentService.EmailDocumentAsync(command.Request, command.UserId, cancellationToken);

            if (!result.Success)
            {
                return Errors.Document.GenerationFailed(result.Message ?? "Email sending failed");
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error emailing document");
            return Errors.Document.GenerationFailed(ex.Message);
        }
    }
}
