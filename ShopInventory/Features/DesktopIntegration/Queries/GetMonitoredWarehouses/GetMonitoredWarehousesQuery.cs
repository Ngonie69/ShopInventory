using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetMonitoredWarehouses;

public sealed record GetMonitoredWarehousesQuery() : IRequest<ErrorOr<List<string>>>;
