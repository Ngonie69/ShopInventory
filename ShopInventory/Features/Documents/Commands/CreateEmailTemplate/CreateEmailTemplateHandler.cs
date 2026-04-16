using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.CreateEmailTemplate;

public sealed class CreateEmailTemplateHandler(
    IDocumentService documentService,
    ILogger<CreateEmailTemplateHandler> logger
) : IRequestHandler<CreateEmailTemplateCommand, ErrorOr<EmailTemplateDto>>
{
    public async Task<ErrorOr<EmailTemplateDto>> Handle(
        CreateEmailTemplateCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var template = await documentService.CreateEmailTemplateAsync(command.Request, cancellationToken);
            return template;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating email template");
            return Errors.Document.EmailTemplateFailed(ex.Message);
        }
    }
}
