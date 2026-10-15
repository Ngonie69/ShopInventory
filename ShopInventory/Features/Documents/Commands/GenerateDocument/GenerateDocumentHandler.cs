using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.Documents;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.GenerateDocument;

public sealed class GenerateDocumentHandler(
    DocumentAttachmentAccessService accessService,
    IDocumentService documentService,
    ILogger<GenerateDocumentHandler> logger
) : IRequestHandler<GenerateDocumentCommand, ErrorOr<GenerateDocumentResponseDto>>
{
    public async Task<ErrorOr<GenerateDocumentResponseDto>> Handle(
        GenerateDocumentCommand command,
        CancellationToken cancellationToken)
    {
        // The document is rendered from the entity, so the caller must be able to read that entity.
        var access = await accessService.AuthorizeEntityAccessAsync(
            command.Request.DocumentType, command.Request.EntityId, isWriteOperation: false, cancellationToken);
        if (access.IsError)
        {
            return access.Errors;
        }

        try
        {
            var result = await documentService.GenerateDocumentAsync(command.Request, command.UserId, cancellationToken);

            if (!result.Success)
            {
                return Errors.Document.GenerationFailed(result.Message ?? "Document generation failed");
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating document");
            return Errors.Document.GenerationFailed(ex.Message);
        }
    }
}
