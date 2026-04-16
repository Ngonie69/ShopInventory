using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetPagedTransferRequests;

public sealed record GetPagedTransferRequestsQuery(
    int Page = 1,
    int PageSize = 20
) : IRequest<ErrorOr<List<InventoryTransferRequestDto>>>;
