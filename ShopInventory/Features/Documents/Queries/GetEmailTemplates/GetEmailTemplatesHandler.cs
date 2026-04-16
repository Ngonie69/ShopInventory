using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Queries.GetEmailTemplates;

public sealed class GetEmailTemplatesHandler(
    IDocumentService documentService,
    ILogger<GetEmailTemplatesHandler> logger
) : IRequestHandler<GetEmailTemplatesQuery, ErrorOr<EmailTemplateListResponseDto>>
{
    public async Task<ErrorOr<EmailTemplateListResponseDto>> Handle(
        GetEmailTemplatesQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await documentService.GetAllEmailTemplatesAsync(query.ActiveOnly, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting email templates");
            return Errors.Document.EmailTemplateFailed(ex.Message);
        }
    }
}
