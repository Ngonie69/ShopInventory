using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Queries.GetAttachments;

public sealed class GetAttachmentsHandler(
    IDocumentService documentService,
    ILogger<GetAttachmentsHandler> logger
) : IRequestHandler<GetAttachmentsQuery, ErrorOr<DocumentAttachmentListResponseDto>>
{
    public async Task<ErrorOr<DocumentAttachmentListResponseDto>> Handle(
        GetAttachmentsQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await documentService.GetAttachmentsAsync(query.EntityType, query.EntityId, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting attachments for {EntityType}/{EntityId}", query.EntityType, query.EntityId);
            return Errors.Document.GenerationFailed(ex.Message);
        }
    }
}
