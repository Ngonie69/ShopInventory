using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransfersByWarehouse;

public sealed record GetTransfersByWarehouseQuery(
    string WarehouseCode,
    DateTime? FromDate = null,
    DateTime? ToDate = null
) : IRequest<ErrorOr<List<InventoryTransferDto>>>;
