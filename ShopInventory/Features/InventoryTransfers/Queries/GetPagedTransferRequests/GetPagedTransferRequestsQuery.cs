using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetPagedTransferRequests;

public sealed record GetPagedTransferRequestsQuery(int Page, int PageSize) : IRequest<ErrorOr<TransferRequestListResponseDto>>;
