using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.UpdateEmailTemplate;

public sealed class UpdateEmailTemplateHandler(
    IDocumentService documentService,
    ILogger<UpdateEmailTemplateHandler> logger
) : IRequestHandler<UpdateEmailTemplateCommand, ErrorOr<EmailTemplateDto>>
{
    public async Task<ErrorOr<EmailTemplateDto>> Handle(
        UpdateEmailTemplateCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var template = await documentService.UpdateEmailTemplateAsync(command.Id, command.Request, cancellationToken);
            return template;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating email template {TemplateId}", command.Id);
            return Errors.Document.EmailTemplateFailed(ex.Message);
        }
    }
}
