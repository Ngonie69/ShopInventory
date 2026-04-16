using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransfersByDateRange;

public sealed record GetTransfersByDateRangeQuery(
    string WarehouseCode,
    DateTime FromDate,
    DateTime ToDate
) : IRequest<ErrorOr<List<InventoryTransferDto>>>;
