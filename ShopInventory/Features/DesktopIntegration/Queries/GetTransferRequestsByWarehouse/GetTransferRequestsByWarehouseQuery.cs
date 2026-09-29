using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransferRequestsByWarehouse;

public sealed record GetTransferRequestsByWarehouseQuery(
    string WarehouseCode,
    DateTime? FromDate = null
) : IRequest<ErrorOr<List<InventoryTransferRequestDto>>>;
