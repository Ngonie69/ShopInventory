using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Queries.GetDefaultTemplate;

public sealed class GetDefaultTemplateHandler(
    IDocumentService documentService,
    ILogger<GetDefaultTemplateHandler> logger
) : IRequestHandler<GetDefaultTemplateQuery, ErrorOr<DocumentTemplateDto>>
{
    public async Task<ErrorOr<DocumentTemplateDto>> Handle(
        GetDefaultTemplateQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var template = await documentService.GetDefaultTemplateAsync(query.DocumentType, cancellationToken);
            if (template == null)
            {
                return Errors.Document.DefaultTemplateNotFound(query.DocumentType);
            }
            return template;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting default template for {DocumentType}", query.DocumentType);
            return Errors.Document.GenerationFailed(ex.Message);
        }
    }
}
