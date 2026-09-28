using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetMonitoredWarehouses;

public sealed class GetMonitoredWarehousesHandler(
    ApplicationDbContext context,
    IOptions<DailyStockSettings> settings) : IRequestHandler<GetMonitoredWarehousesQuery, ErrorOr<List<string>>>
{
    public async Task<ErrorOr<List<string>>> Handle(GetMonitoredWarehousesQuery request, CancellationToken cancellationToken)
    {
        var warehouses = await MonitoredWarehouseList.ReadAsync(context, settings.Value, cancellationToken);

        return warehouses
            .OrderBy(w => w)
            .ToList();
    }
}
