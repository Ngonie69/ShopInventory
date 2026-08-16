namespace ShopInventory.DTOs;

/// <summary>
/// Which handset may sign receipts offline on a fiscal device, as the office sees it.
/// </summary>
public class FiscalDeviceOfflineLeaseDto
{
    public int DeviceId { get; set; }

    /// <summary>Null when no handset is nominated, which means no van may trade out of coverage.</summary>
    public Guid? HolderUserId { get; set; }

    /// <summary>The holder as the office would name it — the van's warehouse code where there is one.</summary>
    public string? HolderLabel { get; set; }

    public DateTime? AssignedAtUtc { get; set; }

    public string? AssignedByName { get; set; }

    /// <summary>
    /// Signed receipts the holder last reported it was still carrying. Null means it has not said, which
    /// is not the same as none.
    /// </summary>
    public int? HolderPendingSales { get; set; }

    public DateTime? HolderLastSeenAtUtc { get; set; }

    /// <summary>
    /// Whether the nomination can be moved right now without stranding signed receipts. False does not
    /// forbid the move; it means the move has to be made deliberately, and says why it matters.
    /// </summary>
    public bool CanHandOver { get; set; }
}

/// <summary>A fiscal device, who is signing offline on it, and who else could be.</summary>
public class FiscalDeviceOfflineLeaseSummaryDto
{
    public FiscalDeviceOfflineLeaseDto Lease { get; set; } = new();

    /// <summary>Active handsets registered against this device, so the office picks rather than types.</summary>
    public List<OfflineSigningCandidateDto> Candidates { get; set; } = [];
}

/// <summary>A handset that could be nominated for a device.</summary>
public class OfflineSigningCandidateDto
{
    public Guid UserId { get; set; }

    public string Label { get; set; } = string.Empty;
}

/// <summary>What the office sends to change which handset may sign offline on a device.</summary>
public class AssignOfflineSigningLeaseRequest
{
    /// <summary>Null to leave nobody nominated, which stops every van signing offline on this device.</summary>
    public Guid? HolderUserId { get; set; }

    /// <summary>
    /// Move it even though the outgoing handset is still carrying signed receipts, or has never said
    /// whether it is. For a handset that is lost or broken and will never report again.
    /// </summary>
    public bool Force { get; set; }
}
