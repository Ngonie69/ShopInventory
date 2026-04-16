using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.SetDefaultTemplate;

public sealed class SetDefaultTemplateHandler(
    IDocumentService documentService,
    ILogger<SetDefaultTemplateHandler> logger
) : IRequestHandler<SetDefaultTemplateCommand, ErrorOr<bool>>
{
    public async Task<ErrorOr<bool>> Handle(
        SetDefaultTemplateCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await documentService.SetDefaultTemplateAsync(command.Id, cancellationToken);
            if (!result)
            {
                return Errors.Document.TemplateNotFound(command.Id);
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error setting default template {TemplateId}", command.Id);
            return Errors.Document.GenerationFailed(ex.Message);
        }
    }
}
