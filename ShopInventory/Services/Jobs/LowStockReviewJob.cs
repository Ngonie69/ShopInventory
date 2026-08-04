using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Configuration;
using ShopInventory.DTOs;

namespace ShopInventory.Services;

/// <summary>
/// Morning sweep for items at or below the reorder threshold, so stock control sees them before the
/// day's loading rather than when an order cannot be filled.
/// </summary>
/// <remarks>
/// Raises one summary notification plus an alert for each of the worst critical items, capped by
/// <see cref="LowStockAlertSettings.CriticalItemNotificationLimit"/>. Silent when nothing is low:
/// an alert that arrives every morning regardless is one nobody reads. The schedule and enablement
/// live on the trigger in <see cref="QuartzConfiguration"/>.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class LowStockReviewJob : IJob
{
    private readonly IServiceProvider _serviceProvider;
    private readonly LowStockAlertSettings _settings;
    private readonly ILogger<LowStockReviewJob> _logger;

    public LowStockReviewJob(
        IServiceProvider serviceProvider,
        IOptions<LowStockAlertSettings> settings,
        ILogger<LowStockReviewJob> logger)
    {
        _serviceProvider = serviceProvider;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var reportService = scope.ServiceProvider.GetRequiredService<IReportService>();

        LowStockAlertReportDto report;
        try
        {
            report = await reportService.GetLowStockAlertsAsync(
                _settings.WarehouseCode,
                _settings.ReorderThreshold,
                context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Nothing to retry against — the next run is tomorrow morning, and the low-stock report
            // is available on demand in the meantime.
            _logger.LogError(ex, "The morning low stock review failed; no notification was raised");
            return;
        }

        if (report.TotalAlerts == 0)
        {
            _logger.LogInformation("Morning low stock review: nothing at or below the reorder threshold");
            return;
        }

        foreach (var item in report.Items)
        {
            _logger.LogWarning(
                "Low stock: {ItemCode} ({ItemName}) in {Warehouse} — {CurrentStock} on hand against a reorder level of {ReorderLevel} [{AlertLevel}]",
                item.ItemCode,
                item.ItemName,
                item.WarehouseCode,
                item.CurrentStock,
                item.ReorderLevel,
                item.AlertLevel);
        }

        _logger.LogWarning(
            "Morning low stock review: {TotalAlerts} item/warehouse line(s) low ({CriticalCount} critical, {WarningCount} warning)",
            report.TotalAlerts,
            report.CriticalCount,
            report.WarningCount);

        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

        try
        {
            await notificationService.CreateNotificationAsync(
                new CreateNotificationRequest
                {
                    Title = BuildSummaryTitle(report),
                    Message = BuildSummaryMessage(report, _settings.ReorderThreshold),
                    Type = report.CriticalCount > 0 ? "Error" : "Warning",
                    // Category and ActionUrl must both pass the audience rules; together these put
                    // the summary in front of Admin and StockController.
                    Category = "LowStock",
                    ActionUrl = "/stock",
                    EntityType = "LowStockReview"
                },
                context.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to raise the summary notification for the morning low stock review");
        }

        // The worst offenders individually, so each one can be clicked straight through to the item.
        // Ordered by what is actually most nearly out, not by whatever order the warehouses returned.
        var criticalItems = report.Items
            .Where(item => string.Equals(item.AlertLevel, "Critical", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.CurrentStock)
            .Take(Math.Max(0, _settings.CriticalItemNotificationLimit))
            .ToList();

        foreach (var item in criticalItems)
        {
            try
            {
                await notificationService.CreateLowStockAlertAsync(
                    item.ItemCode,
                    item.ItemName,
                    item.CurrentStock,
                    item.ReorderLevel,
                    context.CancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to raise the low stock notification for {ItemCode}; the item is in the log above",
                    item.ItemCode);
            }
        }
    }

    private static string BuildSummaryTitle(LowStockAlertReportDto report) =>
        report.CriticalCount > 0
            ? $"Low stock: {report.TotalAlerts} item(s), {report.CriticalCount} critical"
            : $"Low stock: {report.TotalAlerts} item(s)";

    private static string BuildSummaryMessage(LowStockAlertReportDto report, decimal threshold)
    {
        var worst = report.Items
            .OrderBy(item => item.CurrentStock)
            .Take(5)
            .Select(item => $"{item.ItemCode} ({item.CurrentStock:0.##} in {item.WarehouseCode})");

        var lead = $"{report.TotalAlerts} item/warehouse line(s) are at or below a reorder level of {threshold:0.##} " +
                   $"({report.CriticalCount} critical, {report.WarningCount} warning). Lowest: ";

        return lead + string.Join(", ", worst) + ".";
    }
}
