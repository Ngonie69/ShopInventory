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
    string? UploadedByUsername,
    string? UploadedFromLocation,
    Guid? UploadedByUserId,
    // Null for service callers (customer portal), which carry no staff user identity.
    Guid? UserId
) : IRequest<ErrorOr<PodAttachmentListResponseDto>>;
