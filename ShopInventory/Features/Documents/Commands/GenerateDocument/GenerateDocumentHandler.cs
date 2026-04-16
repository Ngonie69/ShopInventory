using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.GenerateDocument;

public sealed class GenerateDocumentHandler(
    IDocumentService documentService,
    ILogger<GenerateDocumentHandler> logger
) : IRequestHandler<GenerateDocumentCommand, ErrorOr<GenerateDocumentResponseDto>>
{
    public async Task<ErrorOr<GenerateDocumentResponseDto>> Handle(
        GenerateDocumentCommand command,
        CancellationToken cancellationToken)
    {
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
