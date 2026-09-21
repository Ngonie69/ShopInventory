using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Configuration;
using ShopInventory.Data;

namespace ShopInventory.Services;

/// <summary>
/// Removes temperature readings older than <see cref="CartrackSettings.TemperatureSampleRetentionDays"/>.
/// </summary>
/// <remarks>
/// Weekly and in batches. The table grows by a few thousand rows a day, so a week's worth of
/// expiry is small, but the first run after a retention change can meet a year of it, and one
/// statement deleting that much holds its locks for as long as it takes.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class CartrackTemperatureRetentionJob(
    IServiceScopeFactory scopeFactory,
    ILogger<CartrackTemperatureRetentionJob> logger) : IJob
{
    public const string JobName = "cartrack-temperature-retention";

    private const int BatchSize = 5000;

    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<IOptions<CartrackSettings>>().Value;

        try
        {
            // Never below a year, whatever the setting says. Cold-chain evidence is asked for
            // long after the fact, and a retention of 7 typed where 730 was meant would delete
            // almost all of it the following Sunday.
            var days = Math.Max(365, settings.TemperatureSampleRetentionDays);
            var cutoff = DateTime.UtcNow.AddDays(-days);

            var removed = await PurgeAsync(db, cutoff, BatchSize, context.CancellationToken);

            if (removed > 0)
            {
                logger.LogInformation(
                    "Removed {Count} temperature reading(s) taken before {Cutoff:yyyy-MM-dd}",
                    removed, cutoff);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Temperature reading retention failed");

            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }

    /// <summary>Deletes every reading taken before <paramref name="cutoffUtc"/>, a batch at a time.</summary>
    public static async Task<int> PurgeAsync(
        ApplicationDbContext db, DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken)
    {
        var total = 0;

        while (true)
        {
            var ids = await db.VehicleTemperatureSamples
                .AsNoTracking()
                .Where(sample => sample.EventAtUtc < cutoffUtc)
                .OrderBy(sample => sample.Id)
                .Select(sample => sample.Id)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (ids.Count == 0)
            {
                return total;
            }

            total += await db.VehicleTemperatureSamples
                .Where(sample => ids.Contains(sample.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }
    }
}
