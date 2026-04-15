using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetTransferRequestsByWarehouse;

public sealed record GetTransferRequestsByWarehouseQuery(string WarehouseCode) : IRequest<ErrorOr<TransferRequestListResponseDto>>;
