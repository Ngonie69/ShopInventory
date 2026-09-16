using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// One running API process, and the build it is running. Written at startup, refreshed on a heartbeat
/// and deleted on a clean shutdown.
/// </summary>
/// <remarks>
/// The row exists so a node can see what the rest of the cluster is running. Quartz's own
/// <c>qrtz_scheduler_state</c> records membership but says nothing about which code a member holds,
/// which is exactly what went unnoticed when a node left on an old build kept taking jobs.
/// </remarks>
[Table("ClusterNodes")]
[Index(nameof(NodeKey), IsUnique = true)]
public class ClusterNodeEntity
{
    public int Id { get; set; }

    /// <summary>Machine and content root, so the two blue/green slots on one box are two nodes.</summary>
    [MaxLength(500)]
    public string NodeKey { get; set; } = string.Empty;

    [MaxLength(200)]
    public string MachineName { get; set; } = string.Empty;

    [MaxLength(400)]
    public string ContentRoot { get; set; } = string.Empty;

    /// <summary>Null for a build no deploy published.</summary>
    public DateTime? BuildTimestampUtc { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }

    /// <summary>What the gate decided at the last heartbeat, so the state is visible without log access.</summary>
    public bool JobsVetoed { get; set; }

    [MaxLength(1000)]
    public string? VetoReason { get; set; }
}
