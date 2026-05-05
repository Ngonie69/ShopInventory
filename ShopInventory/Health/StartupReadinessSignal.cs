namespace ShopInventory.Health;

public sealed class StartupReadinessSignal
{
    private readonly object _gate = new();

    public bool IsReady { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTime? LastUpdatedAtUtc { get; private set; }

    public void MarkReady()
    {
        lock (_gate)
        {
            IsReady = true;
            FailureReason = null;
            LastUpdatedAtUtc = DateTime.UtcNow;
        }
    }

    public void MarkFailed(Exception exception)
    {
        lock (_gate)
        {
            IsReady = false;
            FailureReason = exception.GetBaseException().Message;
            LastUpdatedAtUtc = DateTime.UtcNow;
        }
    }
}