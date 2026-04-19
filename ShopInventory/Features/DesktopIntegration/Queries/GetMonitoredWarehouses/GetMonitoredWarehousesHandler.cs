using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetMonitoredWarehouses;

public sealed class GetMonitoredWarehousesHandler(
    IOptions<DailyStockSettings> settings) : IRequestHandler<GetMonitoredWarehousesQuery, ErrorOr<List<string>>>
{
    public Task<ErrorOr<List<string>>> Handle(GetMonitoredWarehousesQuery request, CancellationToken cancellationToken)
    {
        var warehouses = settings.Value.MonitoredWarehouses
            .OrderBy(w => w)
            .ToList();

        return Task.FromResult<ErrorOr<List<string>>>(warehouses);
    }
}
