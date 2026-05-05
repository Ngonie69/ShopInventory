using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.SetDefaultTemplate;

public sealed class SetDefaultTemplateHandler(
    IDocumentService documentService,
    IAuditService auditService,
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

            try
            {
                await auditService.LogAsync(
                    AuditActions.SetDefaultDocumentTemplate,
                    "DocumentTemplate",
                    command.Id.ToString(),
                    $"Document template {command.Id} set as default",
                    true);
            }
            catch
            {
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
