using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetTransfersByDate;

public sealed record GetTransfersByDateQuery(string WarehouseCode, string Date) : IRequest<ErrorOr<InventoryTransferDateResponseDto>>;
