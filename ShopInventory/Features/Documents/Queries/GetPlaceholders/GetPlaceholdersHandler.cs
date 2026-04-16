using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Queries.GetPlaceholders;

public sealed class GetPlaceholdersHandler(
    IDocumentService documentService,
    ILogger<GetPlaceholdersHandler> logger
) : IRequestHandler<GetPlaceholdersQuery, ErrorOr<TemplatePlaceholdersDto>>
{
    public async Task<ErrorOr<TemplatePlaceholdersDto>> Handle(
        GetPlaceholdersQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await documentService.GetPlaceholdersAsync(query.DocumentType, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting placeholders for {DocumentType}", query.DocumentType);
            return Errors.Document.GenerationFailed(ex.Message);
        }
    }
}
