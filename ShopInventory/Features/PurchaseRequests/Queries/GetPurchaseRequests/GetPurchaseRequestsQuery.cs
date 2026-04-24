using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PurchaseRequests.Queries.GetPurchaseRequests;

public sealed record GetPurchaseRequestsQuery(
    int Page = 1,
    int PageSize = 20,
    DateTime? FromDate = null,
    DateTime? ToDate = null
) : IRequest<ErrorOr<PurchaseRequestListResponseDto>>;