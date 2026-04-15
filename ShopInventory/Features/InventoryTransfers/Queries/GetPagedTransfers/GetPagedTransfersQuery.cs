using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetPagedTransfers;

public sealed record GetPagedTransfersQuery(string WarehouseCode, int Page, int PageSize) : IRequest<ErrorOr<InventoryTransferListResponseDto>>;
