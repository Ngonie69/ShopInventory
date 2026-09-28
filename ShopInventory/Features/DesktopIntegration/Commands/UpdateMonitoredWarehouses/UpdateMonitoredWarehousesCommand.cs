using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.UpdateMonitoredWarehouses;

/// <summary>Replaces the list of warehouses the daily stock snapshot covers.</summary>
public sealed record UpdateMonitoredWarehousesCommand(
    Guid CallerUserId,
    string? CallerName,
    List<string> Warehouses
) : IRequest<ErrorOr<List<string>>>;
