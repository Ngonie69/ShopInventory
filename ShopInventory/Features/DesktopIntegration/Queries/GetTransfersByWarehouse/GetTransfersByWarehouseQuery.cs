using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransfersByWarehouse;

public sealed record GetTransfersByWarehouseQuery(
    string WarehouseCode
) : IRequest<ErrorOr<List<InventoryTransferDto>>>;
