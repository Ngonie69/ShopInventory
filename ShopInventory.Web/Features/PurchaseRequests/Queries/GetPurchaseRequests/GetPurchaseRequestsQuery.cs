using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.PurchaseRequests.Queries.GetPurchaseRequests;

public sealed record GetPurchaseRequestsQuery(
    int Page = 1,
    int PageSize = 20,
    DateTime? FromDate = null,
    DateTime? ToDate = null
) : IRequest<ErrorOr<PurchaseRequestListResponse>>;