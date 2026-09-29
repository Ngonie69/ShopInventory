using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetTransfersByWarehouse;

public sealed record GetTransfersByWarehouseQuery(string WarehouseCode, DateTime? FromDate = null, DateTime? ToDate = null) : IRequest<ErrorOr<InventoryTransferListResponseDto>>;
