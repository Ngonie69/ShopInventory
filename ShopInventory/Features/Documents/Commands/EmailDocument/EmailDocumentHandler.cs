using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.EmailDocument;

public sealed class EmailDocumentHandler(
    IDocumentService documentService,
    ILogger<EmailDocumentHandler> logger
) : IRequestHandler<EmailDocumentCommand, ErrorOr<GenerateDocumentResponseDto>>
{
    public async Task<ErrorOr<GenerateDocumentResponseDto>> Handle(
        EmailDocumentCommand command,
        CancellationToken cancellationToken)
    {
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
