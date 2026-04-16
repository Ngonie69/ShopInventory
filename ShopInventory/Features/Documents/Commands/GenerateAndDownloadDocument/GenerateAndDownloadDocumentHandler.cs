using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.GenerateAndDownloadDocument;

public sealed class GenerateAndDownloadDocumentHandler(
    IDocumentService documentService,
    ILogger<GenerateAndDownloadDocumentHandler> logger
) : IRequestHandler<GenerateAndDownloadDocumentCommand, ErrorOr<FileDownloadResult>>
{
    public async Task<ErrorOr<FileDownloadResult>> Handle(
        GenerateAndDownloadDocumentCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await documentService.GenerateDocumentAsync(command.Request, command.UserId, cancellationToken);

            if (!result.Success || result.FileContent == null)
            {
                return Errors.Document.GenerationFailed(result.Message ?? "Document download failed");
            }

            return new FileDownloadResult(
                result.FileContent,
                result.FileName ?? "document.pdf",
                "application/pdf");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating and downloading document");
            return Errors.Document.GenerationFailed(ex.Message);
        }
    }
}
