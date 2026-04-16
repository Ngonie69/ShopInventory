using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetAvailableBatches;

public sealed record GetAvailableBatchesQuery(
    string WarehouseCode,
    string ItemCode
) : IRequest<ErrorOr<List<AvailableBatchWithReservationsDto>>>;
