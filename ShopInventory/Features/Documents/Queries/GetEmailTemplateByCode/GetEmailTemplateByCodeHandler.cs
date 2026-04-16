using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Queries.GetEmailTemplateByCode;

public sealed class GetEmailTemplateByCodeHandler(
    IDocumentService documentService,
    ILogger<GetEmailTemplateByCodeHandler> logger
) : IRequestHandler<GetEmailTemplateByCodeQuery, ErrorOr<EmailTemplateDto>>
{
    public async Task<ErrorOr<EmailTemplateDto>> Handle(
        GetEmailTemplateByCodeQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var template = await documentService.GetEmailTemplateByCodeAsync(query.TemplateCode, cancellationToken);
            if (template == null)
            {
                return Errors.Document.EmailTemplateNotFound(query.TemplateCode);
            }
            return template;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting email template {TemplateCode}", query.TemplateCode);
            return Errors.Document.EmailTemplateFailed(ex.Message);
        }
    }
}
