using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Queries.GetDocumentHistory;

public sealed class GetDocumentHistoryHandler(
    IDocumentService documentService,
    ILogger<GetDocumentHistoryHandler> logger
) : IRequestHandler<GetDocumentHistoryQuery, ErrorOr<DocumentHistoryListResponseDto>>
{
    public async Task<ErrorOr<DocumentHistoryListResponseDto>> Handle(
        GetDocumentHistoryQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await documentService.GetDocumentHistoryAsync(
                query.DocumentType, query.EntityId, query.Page, query.PageSize, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting document history");
            return Errors.Document.GenerationFailed(ex.Message);
        }
    }
}
