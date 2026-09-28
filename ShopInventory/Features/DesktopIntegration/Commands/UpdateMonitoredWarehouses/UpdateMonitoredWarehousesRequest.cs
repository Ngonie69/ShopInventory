namespace ShopInventory.Features.DesktopIntegration.Commands.UpdateMonitoredWarehouses;

/// <summary>The body of <c>PUT api/DesktopIntegration/stock/monitored-warehouses</c>: the whole list, not a change to it.</summary>
public sealed record UpdateMonitoredWarehousesRequest(List<string>? Warehouses);
