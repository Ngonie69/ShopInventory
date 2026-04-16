using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.DeleteTemplate;

public sealed class DeleteTemplateHandler(
    IDocumentService documentService,
    ILogger<DeleteTemplateHandler> logger
) : IRequestHandler<DeleteTemplateCommand, ErrorOr<bool>>
{
    public async Task<ErrorOr<bool>> Handle(
        DeleteTemplateCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await documentService.DeleteTemplateAsync(command.Id, cancellationToken);
            if (!result)
            {
                return Errors.Document.TemplateNotFound(command.Id);
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting template {TemplateId}", command.Id);
            return Errors.Document.GenerationFailed(ex.Message);
        }
    }
}
