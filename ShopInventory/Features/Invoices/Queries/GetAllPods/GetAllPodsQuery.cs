using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Queries.GetAllPods;

public sealed record GetAllPodsQuery(
    int Page,
    int PageSize,
    string? CardCode,
    DateTime? FromDate,
    DateTime? ToDate,
    string? Search,
    Guid? UploadedByUserId,
    Guid UserId
) : IRequest<ErrorOr<PodAttachmentListResponseDto>>;
