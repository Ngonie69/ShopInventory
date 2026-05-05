using System.Collections.Concurrent;

namespace ShopInventory.Services;

public sealed class BackgroundWorkerHealthRegistry
{
    private readonly ConcurrentDictionary<string, WorkerState> _workers = new(StringComparer.Ordinal);

    public void RegisterWorker(string workerName, bool critical, TimeSpan healthyWindow)
    {
        var now = DateTime.UtcNow;
        _workers.AddOrUpdate(
            workerName,
            _ => new WorkerState(workerName, critical, healthyWindow, "Starting", now, null, null, null, 0),
            (_, current) => current with
            {
                IsCritical = critical,
                HealthyWindow = healthyWindow,
                LastHeartbeatUtc = now
            });
    }

    public void MarkLeader(string workerName)
    {
        var now = DateTime.UtcNow;
        Update(workerName, current => current with
        {
            Mode = "Leader",
            LastHeartbeatUtc = now
        });
    }

    public void MarkStandby(string workerName)
    {
        var now = DateTime.UtcNow;
        Update(workerName, current => current with
        {
            Mode = "Standby",
            LastHeartbeatUtc = now
        });
    }

    public void MarkSuccessfulRun(string workerName)
    {
        var now = DateTime.UtcNow;
        Update(workerName, current => current with
        {
            Mode = "Leader",
            LastHeartbeatUtc = now,
            LastSuccessfulRunUtc = now,
            LastError = null,
            ConsecutiveFailures = 0
        });
    }

    public void MarkFailure(string workerName, Exception exception)
    {
        var now = DateTime.UtcNow;
        Update(workerName, current => current with
        {
            Mode = "Leader",
            LastHeartbeatUtc = now,
            LastFailureUtc = now,
            LastError = exception.GetBaseException().Message,
            ConsecutiveFailures = current.ConsecutiveFailures + 1
        });
    }

    public void MarkStopped(string workerName)
    {
        var now = DateTime.UtcNow;
        Update(workerName, current => current with
        {
            Mode = "Stopped",
            LastHeartbeatUtc = now
        });
    }

    public IReadOnlyList<WorkerSnapshot> GetSnapshots()
    {
        return _workers.Values
            .OrderBy(worker => worker.WorkerName, StringComparer.Ordinal)
            .Select(worker => new WorkerSnapshot(
                worker.WorkerName,
                worker.IsCritical,
                worker.HealthyWindow,
                worker.Mode,
                worker.LastHeartbeatUtc,
                worker.LastSuccessfulRunUtc,
                worker.LastFailureUtc,
                worker.LastError,
                worker.ConsecutiveFailures))
            .ToList();
    }

    private void Update(string workerName, Func<WorkerState, WorkerState> update)
    {
        _workers.AddOrUpdate(
            workerName,
            _ => update(new WorkerState(workerName, true, TimeSpan.FromMinutes(2), "Starting", DateTime.UtcNow, null, null, null, 0)),
            (_, current) => update(current));
    }

    public sealed record WorkerSnapshot(
        string WorkerName,
        bool IsCritical,
        TimeSpan HealthyWindow,
        string Mode,
        DateTime LastHeartbeatUtc,
        DateTime? LastSuccessfulRunUtc,
        DateTime? LastFailureUtc,
        string? LastError,
        int ConsecutiveFailures);

    private sealed record WorkerState(
        string WorkerName,
        bool IsCritical,
        TimeSpan HealthyWindow,
        string Mode,
        DateTime LastHeartbeatUtc,
        DateTime? LastSuccessfulRunUtc,
        DateTime? LastFailureUtc,
        string? LastError,
        int ConsecutiveFailures);
}