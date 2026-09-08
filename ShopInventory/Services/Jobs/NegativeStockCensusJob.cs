using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Counts, once a day, how much stock SAP is holding below zero.
/// </summary>
/// <remarks>
/// <para><b>Why this exists at all.</b> Everything else built against negative stock is a guard: it
/// stops a document that would take stock under. None of them can say whether they worked. This is
/// the outcome, and it is the only figure that can settle the question — a claim that negative stock
/// is fixed is worth nothing without a number that was falling while it was being fixed.</para>
///
/// <para><b>The same measurement as the report script.</b> It asks
/// <see cref="ISAPServiceLayerClient.GetNegativeStockAsync"/>, which sends the statement
/// <c>scripts/NegativeStock/report_negative_stock.py</c> sends, through the same SAP object. So the
/// baseline somebody takes by hand and the series this records are comparable by construction rather
/// than by hoping two queries were written the same way.</para>
///
/// <para><b>Reports, never corrects.</b> There is no automatic repair for negative stock and there
/// should not be: writing a figure back would hide the movement that caused it. Every row is a
/// question for a person.</para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class NegativeStockCensusJob(
    IServiceProvider serviceProvider,
    IOptions<DailyStockSettings> dailyStock,
    ILogger<NegativeStockCensusJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var sapClient = scope.ServiceProvider.GetRequiredService<ISAPServiceLayerClient>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var observedOn = StockLedgerDay.Today(dailyStock.Value.StockFetchTimeCAT);

        List<StockQuantityDto> negatives;
        try
        {
            negatives = await sapClient.GetNegativeStockAsync(context.CancellationToken);
        }
        catch (Exception ex)
        {
            // Nothing else depends on this and a missed day is visible as a gap in the series, which
            // is honest. Inventing a zero would be worse than missing one: it would read as a day
            // with no negative stock.
            logger.LogWarning(ex, "Could not count negative stock in SAP today");
            return;
        }

        // Company-wide read, narrowed to the warehouses this system actually sells from. A negative
        // in a warehouse nobody here touches is real but is not this system's to answer for, and
        // mixing the two makes the trend unreadable.
        var monitored = dailyStock.Value.MonitoredWarehouses
            .Select(code => code.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var relevant = negatives
            .Where(row => !string.IsNullOrWhiteSpace(row.ItemCode)
                       && !string.IsNullOrWhiteSpace(row.WarehouseCode)
                       && monitored.Contains(row.WarehouseCode!.Trim()))
            .ToList();

        // Replace the day rather than append to it, so a manual re-run corrects the day instead of
        // doubling it.
        var existing = await db.NegativeStockObservations
            .Where(row => row.ObservedOn == observedOn)
            .ToListAsync(CancellationToken.None);

        if (existing.Count > 0)
        {
            db.NegativeStockObservations.RemoveRange(existing);
        }

        foreach (var row in relevant)
        {
            db.NegativeStockObservations.Add(new NegativeStockObservationEntity
            {
                ObservedOn = observedOn,
                WarehouseCode = row.WarehouseCode!.Trim(),
                ItemCode = row.ItemCode!.Trim(),
                ItemName = row.ItemName,
                OnHand = row.InStock,
                Committed = row.Committed
            });
        }

        await db.SaveChangesAsync(CancellationToken.None);

        if (relevant.Count == 0)
        {
            logger.LogInformation(
                "Negative stock census for {ObservedOn:yyyy-MM-dd}: nothing below zero in any monitored warehouse",
                observedOn);
            return;
        }

        var byWarehouse = relevant
            .GroupBy(row => row.WarehouseCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Warehouse = group.Key, Rows = group.Count(), Units = group.Sum(row => row.InStock) })
            .OrderBy(entry => entry.Units)
            .ToList();

        logger.LogWarning(
            "Negative stock census for {ObservedOn:yyyy-MM-dd}: {RowCount} item/warehouse rows below zero "
            + "across {WarehouseCount} warehouses ({Detail})",
            observedOn,
            relevant.Count,
            byWarehouse.Count,
            string.Join(", ", byWarehouse.Select(entry => $"{entry.Warehouse} {entry.Units:N2}")));

        await NotifyAsync(notifications, observedOn, relevant.Count, byWarehouse.Count);
    }

    /// <summary>
    /// Tells somebody, once a day and only when there is something to tell.
    /// </summary>
    /// <remarks>
    /// One notification for the whole census rather than one per row. A warehouse that has gone
    /// badly wrong can hold hundreds of items below zero, and a bell that rings hundreds of times
    /// buries every other module — which is exactly the failure the per-document broadcasts used to
    /// cause here.
    /// </remarks>
    private async Task NotifyAsync(
        INotificationService notifications,
        DateTime observedOn,
        int rowCount,
        int warehouseCount)
    {
        try
        {
            await notifications.CreateNotificationAsync(new CreateNotificationRequest
            {
                Title = "Negative stock in SAP",
                Message =
                    $"{rowCount} item/warehouse combination(s) across {warehouseCount} warehouse(s) were below "
                    + $"zero in SAP on {observedOn:yyyy-MM-dd}.",
                Type = "Warning",
                Category = "Inventory",
                ActionUrl = "/reports/negative-stock"
            });
        }
        catch (Exception ex)
        {
            // The count is recorded either way, and that is the part that matters.
            logger.LogWarning(ex, "Could not raise the negative stock notification");
        }
    }
}
