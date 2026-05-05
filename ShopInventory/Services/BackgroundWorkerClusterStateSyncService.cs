using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

public sealed class BackgroundWorkerClusterStateSyncService(
    IServiceScopeFactory scopeFactory,
    BackgroundWorkerHealthRegistry healthRegistry,
    RuntimeInstanceIdentity runtimeInstanceIdentity,
    ILogger<BackgroundWorkerClusterStateSyncService> logger) : BackgroundService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(24);
    private DateTime _lastPrunedUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Background worker cluster state sync started for instance {InstanceId}",
            runtimeInstanceIdentity.InstanceId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncStatesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to sync background worker cluster state");
            }

            try
            {
                await Task.Delay(SyncInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        try
        {
            await SyncStatesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Final background worker cluster state sync failed during shutdown");
        }

        logger.LogInformation(
            "Background worker cluster state sync stopped for instance {InstanceId}",
            runtimeInstanceIdentity.InstanceId);
    }

    private async Task SyncStatesAsync(CancellationToken cancellationToken)
    {
        var snapshots = healthRegistry.GetSnapshots();
        if (snapshots.Count == 0)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var workerNames = snapshots.Select(snapshot => snapshot.WorkerName).ToArray();
        var existingStates = await dbContext.BackgroundWorkerClusterStates
            .AsTracking()
            .Where(state => state.InstanceId == runtimeInstanceIdentity.InstanceId && workerNames.Contains(state.WorkerName))
            .ToDictionaryAsync(state => state.WorkerName, StringComparer.Ordinal, cancellationToken);

        var utcNow = DateTime.UtcNow;
        foreach (var snapshot in snapshots)
        {
            if (!existingStates.TryGetValue(snapshot.WorkerName, out var state))
            {
                state = new BackgroundWorkerClusterStateEntity
                {
                    WorkerName = snapshot.WorkerName,
                    InstanceId = runtimeInstanceIdentity.InstanceId,
                    MachineName = runtimeInstanceIdentity.MachineName,
                    ProcessId = runtimeInstanceIdentity.ProcessId,
                    StartedAtUtc = runtimeInstanceIdentity.StartedAtUtc
                };

                dbContext.BackgroundWorkerClusterStates.Add(state);
                existingStates.Add(snapshot.WorkerName, state);
            }

            state.IsCritical = snapshot.IsCritical;
            state.HealthyWindowSeconds = Math.Max(1, (int)Math.Ceiling(snapshot.HealthyWindow.TotalSeconds));
            state.Mode = snapshot.Mode;
            state.LastHeartbeatUtc = snapshot.LastHeartbeatUtc;
            state.LastSuccessfulRunUtc = snapshot.LastSuccessfulRunUtc;
            state.LastFailureUtc = snapshot.LastFailureUtc;
            state.LastError = snapshot.LastError;
            state.ConsecutiveFailures = snapshot.ConsecutiveFailures;
            state.UpdatedAtUtc = utcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (utcNow - _lastPrunedUtc < TimeSpan.FromMinutes(10))
        {
            return;
        }

        await dbContext.BackgroundWorkerClusterStates
            .Where(state => state.LastHeartbeatUtc < utcNow - RetentionWindow)
            .ExecuteDeleteAsync(cancellationToken);

        _lastPrunedUtc = utcNow;
    }
}