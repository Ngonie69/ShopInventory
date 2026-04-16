using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Queries.GetSignatures;

public sealed class GetSignaturesHandler(
    IDocumentService documentService,
    ILogger<GetSignaturesHandler> logger
) : IRequestHandler<GetSignaturesQuery, ErrorOr<DocumentSignatureListResponseDto>>
{
    public async Task<ErrorOr<DocumentSignatureListResponseDto>> Handle(
        GetSignaturesQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await documentService.GetSignaturesAsync(query.DocumentType, query.DocumentId, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting signatures for {DocumentType}/{DocumentId}", query.DocumentType, query.DocumentId);
            return Errors.Document.GenerationFailed(ex.Message);
        }
    }
}
