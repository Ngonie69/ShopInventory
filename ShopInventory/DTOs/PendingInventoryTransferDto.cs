namespace ShopInventory.DTOs;

/// <summary>
/// A direct inventory transfer held locally while it moves through the approval process.
/// </summary>
public class PendingInventoryTransferDto
{
    public Guid Id { get; set; }

    /// <summary>Reference for the draft while it has no SAP document number, e.g. DT-2026-00007.</summary>
    public string? DraftNumber { get; set; }

    public string? FromWarehouse { get; set; }
    public string? ToWarehouse { get; set; }

    /// <summary>AwaitingApproval, Approved, Rejected, Posted, PostFailed or Cancelled.</summary>
    public string Status { get; set; } = string.Empty;

    public string CreatedByName { get; set; } = string.Empty;
    public string? CreatedByRole { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? DecidedAtUtc { get; set; }

    public int LineCount { get; set; }
    public decimal TotalQuantity { get; set; }
    public string? Comments { get; set; }
    public DateTime? DocDate { get; set; }
    public DateTime? DueDate { get; set; }

    /// <summary>Set once the transfer has posted to SAP.</summary>
    public int? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }
    public DateTime? PostedAtUtc { get; set; }

    /// <summary>When a post to SAP was last started, whatever came of it. Null if never tried.</summary>
    public DateTime? LastAttemptedAtUtc { get; set; }

    public string? LastError { get; set; }

    public Guid? ApprovalRequestId { get; set; }
    public string? ApprovalTemplateName { get; set; }

    /// <summary>Aggregate approval status: Pending, Approved, NotApproved, Generated or Cancelled.</summary>
    public string? ApprovalStatus { get; set; }

    public List<ApprovalStageProgressDto> ApprovalStages { get; set; } = [];

    /// <summary>Whether the caller may record a decision on this transfer right now.</summary>
    public bool CanCurrentUserDecide { get; set; }

    public List<PendingInventoryTransferLineDto> Lines { get; set; } = [];
}

public class PendingInventoryTransferLineDto
{
    public int LineNum { get; set; }
    public string? ItemCode { get; set; }

    /// <summary>
    /// Item name resolved from the local item cache when the draft is read; the stored payload
    /// only carries item codes, so this is blank for items the cache has not seen.
    /// </summary>
    public string? ItemDescription { get; set; }

    public decimal Quantity { get; set; }
    public string? UoMCode { get; set; }
    public string? FromWarehouseCode { get; set; }
    public string? ToWarehouseCode { get; set; }
}

public class PendingInventoryTransferListResponseDto
{
    public List<PendingInventoryTransferDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

/// <summary>
/// Returned when a transfer is submitted and parked for approval.
/// </summary>
public class PendingInventoryTransferSubmittedResponseDto
{
    public string Message { get; set; } = string.Empty;
    public PendingInventoryTransferDto? PendingTransfer { get; set; }
    public string? StatusUrl { get; set; }
}

/// <summary>
/// Result of approving or rejecting a pending direct transfer.
/// </summary>
public class PendingInventoryTransferDecisionResponseDto
{
    public string Message { get; set; } = string.Empty;
    public Guid PendingTransferId { get; set; }
    public string Decision { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool ApprovalProcessComplete { get; set; }

    /// <summary>Populated once the final approval posts the transfer to SAP.</summary>
    public InventoryTransferDto? Transfer { get; set; }
}

public class SubmitPendingTransferDecisionDto
{
    /// <summary>Approved or NotApproved.</summary>
    public string Decision { get; set; } = string.Empty;
    public Guid? StageId { get; set; }
    public string? Remarks { get; set; }
}
