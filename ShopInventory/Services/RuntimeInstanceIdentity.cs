namespace ShopInventory.Services;

public sealed class RuntimeInstanceIdentity
{
    public RuntimeInstanceIdentity()
    {
        MachineName = Environment.MachineName;
        ProcessId = Environment.ProcessId;
        StartedAtUtc = DateTime.UtcNow;
        InstanceId = $"{MachineName}-{ProcessId}-{Guid.NewGuid():N}";
    }

    public string InstanceId { get; }

    public string MachineName { get; }

    public int ProcessId { get; }

    public DateTime StartedAtUtc { get; }
}