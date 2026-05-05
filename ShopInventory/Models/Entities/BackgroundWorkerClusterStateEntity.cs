using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

[Table("BackgroundWorkerClusterStates")]
[Index(nameof(LastHeartbeatUtc))]
[Index(nameof(WorkerName), nameof(Mode), nameof(LastHeartbeatUtc))]
public class BackgroundWorkerClusterStateEntity
{
    [Required]
    [MaxLength(100)]
    public string WorkerName { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string InstanceId { get; set; } = string.Empty;

    public bool IsCritical { get; set; }

    public int HealthyWindowSeconds { get; set; }

    [Required]
    [MaxLength(32)]
    public string Mode { get; set; } = string.Empty;

    public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastSuccessfulRunUtc { get; set; }

    public DateTime? LastFailureUtc { get; set; }

    [MaxLength(2000)]
    public string? LastError { get; set; }

    public int ConsecutiveFailures { get; set; }

    [Required]
    [MaxLength(100)]
    public string MachineName { get; set; } = string.Empty;

    public int ProcessId { get; set; }

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}