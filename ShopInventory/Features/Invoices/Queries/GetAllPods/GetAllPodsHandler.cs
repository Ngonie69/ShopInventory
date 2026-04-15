using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Queries.GetAllPods;

public sealed class GetAllPodsHandler(
    IDocumentService documentService
) : IRequestHandler<GetAllPodsQuery, ErrorOr<PodAttachmentListResponseDto>>
{
    public async Task<ErrorOr<PodAttachmentListResponseDto>> Handle(
        GetAllPodsQuery request,
        CancellationToken cancellationToken)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize < 1 ? 20 : request.PageSize > 100 ? 100 : request.PageSize;

        var result = await documentService.GetAllPodAttachmentsAsync(
            page, pageSize, request.CardCode, cancellationToken,
            request.FromDate, request.ToDate, request.Search, request.UploadedByUserId);

        return result;
    }
}
