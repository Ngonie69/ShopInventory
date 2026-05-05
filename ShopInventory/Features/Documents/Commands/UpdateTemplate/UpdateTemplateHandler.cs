using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.UpdateTemplate;

public sealed class UpdateTemplateHandler(
    IDocumentService documentService,
    IAuditService auditService,
    ILogger<UpdateTemplateHandler> logger
) : IRequestHandler<UpdateTemplateCommand, ErrorOr<DocumentTemplateDto>>
{
    public async Task<ErrorOr<DocumentTemplateDto>> Handle(
        UpdateTemplateCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var template = await documentService.UpdateTemplateAsync(command.Id, command.Request, cancellationToken);

            try
            {
                await auditService.LogAsync(
                    AuditActions.UpdateDocumentTemplate,
                    "DocumentTemplate",
                    template.Id.ToString(),
                    $"Document template '{template.Name}' updated for {template.DocumentType}",
                    true);
            }
            catch
            {
            }

            return template;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating template {TemplateId}", command.Id);
            return Errors.Document.TemplateNotFound(command.Id);
        }
    }
}
