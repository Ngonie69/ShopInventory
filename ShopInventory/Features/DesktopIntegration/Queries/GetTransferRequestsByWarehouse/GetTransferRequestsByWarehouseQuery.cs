using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransferRequestsByWarehouse;

public sealed record GetTransferRequestsByWarehouseQuery(
    string WarehouseCode
) : IRequest<ErrorOr<List<InventoryTransferRequestDto>>>;
