using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Queries.GetTemplates;

public sealed class GetTemplatesHandler(
    IDocumentService documentService,
    ILogger<GetTemplatesHandler> logger
) : IRequestHandler<GetTemplatesQuery, ErrorOr<DocumentTemplateListResponseDto>>
{
    public async Task<ErrorOr<DocumentTemplateListResponseDto>> Handle(
        GetTemplatesQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await documentService.GetAllTemplatesAsync(
                query.DocumentType, query.ActiveOnly, query.Page, query.PageSize, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting document templates");
            return Errors.Document.GenerationFailed(ex.Message);
        }
    }
}
