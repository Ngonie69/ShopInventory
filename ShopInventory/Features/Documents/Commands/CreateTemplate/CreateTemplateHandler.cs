using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.CreateTemplate;

public sealed class CreateTemplateHandler(
    IDocumentService documentService,
    IAuditService auditService,
    ILogger<CreateTemplateHandler> logger
) : IRequestHandler<CreateTemplateCommand, ErrorOr<DocumentTemplateDto>>
{
    public async Task<ErrorOr<DocumentTemplateDto>> Handle(
        CreateTemplateCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var template = await documentService.CreateTemplateAsync(command.Request, command.UserId, cancellationToken);

            try
            {
                await auditService.LogAsync(
                    AuditActions.CreateDocumentTemplate,
                    "DocumentTemplate",
                    template.Id.ToString(),
                    $"Document template '{template.Name}' created for {template.DocumentType}",
                    true);
            }
            catch
            {
            }

            return template;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating document template");
            return Errors.Document.GenerationFailed(ex.Message);
        }
    }
}
