using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Queries.GetTemplateById;

public sealed class GetTemplateByIdHandler(
    IDocumentService documentService,
    ILogger<GetTemplateByIdHandler> logger
) : IRequestHandler<GetTemplateByIdQuery, ErrorOr<DocumentTemplateDto>>
{
    public async Task<ErrorOr<DocumentTemplateDto>> Handle(
        GetTemplateByIdQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var template = await documentService.GetTemplateByIdAsync(query.Id, cancellationToken);
            if (template == null)
            {
                return Errors.Document.TemplateNotFound(query.Id);
            }
            return template;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting template {TemplateId}", query.Id);
            return Errors.Document.GenerationFailed(ex.Message);
        }
    }
}
