using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.UpdateMonitoredWarehouses;

public sealed class UpdateMonitoredWarehousesHandler(
    ApplicationDbContext db,
    IOptions<DailyStockSettings> settings,
    IAuditService auditService,
    ILogger<UpdateMonitoredWarehousesHandler> logger)
    : IRequestHandler<UpdateMonitoredWarehousesCommand, ErrorOr<List<string>>>
{
    public async Task<ErrorOr<List<string>>> Handle(
        UpdateMonitoredWarehousesCommand request, CancellationToken cancellationToken)
    {
        var who = string.IsNullOrWhiteSpace(request.CallerName) ? request.CallerUserId.ToString() : request.CallerName;

        var before = await MonitoredWarehouseList.ReadAsync(db, settings.Value, cancellationToken);
        var saved = await MonitoredWarehouseList.SaveAsync(db, request.Warehouses, request.CallerUserId, cancellationToken);

        var added = saved.Warehouses.Except(before, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = before.Except(saved.Warehouses, StringComparer.OrdinalIgnoreCase).ToList();
        var change = Describe(added, removed);

        logger.LogInformation(
            "Monitored warehouses changed by {User}: {Change}. Now {Count}: {Warehouses}",
            who, change, saved.Warehouses.Count, string.Join(", ", saved.Warehouses));

        try
        {
            await auditService.LogAsync(
                AuditActions.UpdateMonitoredWarehouses,
                "DailyStock",
                null,
                $"Monitored warehouses changed by {who}: {change}",
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the monitored warehouse change");
        }

        return saved.Warehouses;
    }

    private static string Describe(List<string> added, List<string> removed)
    {
        var parts = new List<string>();
        if (added.Count > 0) parts.Add($"added {string.Join(", ", added)}");
        if (removed.Count > 0) parts.Add($"removed {string.Join(", ", removed)}");
        return parts.Count > 0 ? string.Join("; ", parts) : "no change";
    }
}
