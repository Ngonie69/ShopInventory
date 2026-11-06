namespace ShopInventory.DTOs;

/// <summary>
/// One inventory transfer keyed in the SAP client, approved by SAP's approval procedure and still
/// waiting as a draft for somebody to add it.
/// </summary>
public sealed class SapTransferApprovalListItemDto
{
    /// <summary>The draft's DocEntry — what the detail and the add are keyed on.</summary>
    public int DraftEntry { get; set; }

    /// <summary>The draft's own number, as the B1 client shows it. Not the transfer's number.</summary>
    public int DraftNum { get; set; }

    public DateTime? DocDate { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? FromWarehouse { get; set; }
    public string? ToWarehouse { get; set; }
    public string? Comments { get; set; }

    /// <summary>The draft's own approval state: <c>Approved</c> for every row the list returns.</summary>
    public string DraftStatus { get; set; } = string.Empty;

    public bool HasAttachment { get; set; }

    /// <summary>The newest approval request holding the draft; null when SAP has none on record.</summary>
    public int? RequestCode { get; set; }

    /// <summary>Every approval request holding the draft — one per approval template that matched it.</summary>
    public List<int> RequestCodes { get; set; } = [];

    public string? RequestStatus { get; set; }
    public string? RequestRemarks { get; set; }
    public DateTime? RequestedDate { get; set; }
    public string? RequestedTime { get; set; }
    public int? TemplateCode { get; set; }
    public string? TemplateName { get; set; }
    public int? StageCode { get; set; }
    public string? StageName { get; set; }
    public int? OriginatorId { get; set; }
    public string? OriginatorUserCode { get; set; }
    public string? OriginatorName { get; set; }

    /// <summary>
    /// SAP would take the add as far as this read can tell. The detail checks more — the batch picks
    /// and the source warehouse of every line — and the add checks all of it again.
    /// </summary>
    public bool CanAdd { get; set; }

    /// <summary>Why the row cannot be added, when it cannot.</summary>
    public string? StatusNote { get; set; }
}

public sealed class SapTransferApprovalListResponseDto
{
    public List<SapTransferApprovalListItemDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public string? Warehouse { get; set; }

    /// <summary>
    /// The source warehouses the caller's queue is limited to; null when they see every warehouse.
    /// A depot controller sees only transfers leaving the warehouses on their account.
    /// </summary>
    public List<string>? ScopedWarehouses { get; set; }

    /// <summary>The DocEntry to pass as <c>beforeDocEntry</c> for the next page; null at the end of the queue.</summary>
    public int? NextCursor { get; set; }
}

/// <summary>A draft with its lines, the approvals behind it and the transfer request it was copied from.</summary>
public sealed class SapTransferApprovalDetailDto
{
    public SapTransferApprovalListItemDto Draft { get; set; } = new();
    public List<SapTransferDraftLineDto> Lines { get; set; } = [];
    public List<SapTransferApprovalRequestDto> Approvals { get; set; } = [];

    /// <summary>The transfer requests the draft's lines were copied from in the B1 client.</summary>
    public List<SapTransferLinkedRequestDto> LinkedRequests { get; set; } = [];

    /// <summary>
    /// True when SAP could not say which items it manages by batch or serial, so the page cannot tell
    /// whether every pick is complete. The add still asks SAP, which decides.
    /// </summary>
    public bool SelectionUnchecked { get; set; }
}

public sealed class SapTransferDraftLineDto
{
    public int LineNum { get; set; }
    public string? ItemCode { get; set; }
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public string? UoMCode { get; set; }
    public string? FromWarehouse { get; set; }
    public string? ToWarehouse { get; set; }

    /// <summary>Null when SAP's item flags could not be read.</summary>
    public bool? ManagesBatches { get; set; }

    /// <summary>Null when SAP's item flags could not be read.</summary>
    public bool? ManagesSerials { get; set; }

    public List<SapTransferDraftBatchDto> Batches { get; set; } = [];

    /// <summary>How many serial numbers are picked on the line.</summary>
    public int SerialCount { get; set; }

    /// <summary>
    /// The item is batch- or serial-managed and the line's picks do not cover its quantity. SAP refuses
    /// the whole transfer for one such line.
    /// </summary>
    public bool SelectionMissing { get; set; }

    /// <summary>The transfer request the line was copied from, when it was.</summary>
    public int? BaseRequestDocEntry { get; set; }
}

public sealed class SapTransferDraftBatchDto
{
    public string? BatchNumber { get; set; }
    public decimal Quantity { get; set; }
}

public sealed class SapTransferApprovalRequestDto
{
    public int Code { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? TemplateCode { get; set; }
    public string? TemplateName { get; set; }
    public int? StageCode { get; set; }
    public string? StageName { get; set; }
    public string? Remarks { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedTime { get; set; }
    public List<SapTransferApprovalDecisionDto> Decisions { get; set; } = [];
}

public sealed class SapTransferApprovalDecisionDto
{
    public int? StageCode { get; set; }
    public string? StageName { get; set; }
    public int? UserId { get; set; }
    public string? UserCode { get; set; }
    public string? UserName { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Remarks { get; set; }
    public DateTime? DecidedDate { get; set; }
    public string? DecidedTime { get; set; }
}

/// <summary>A transfer request the draft was copied from, as SAP holds it now.</summary>
public sealed class SapTransferLinkedRequestDto
{
    public int DocEntry { get; set; }

    /// <summary>Null when the request could not be read.</summary>
    public int? DocNum { get; set; }

    /// <summary><c>Open</c>, <c>Closed</c>, or null when the request could not be read.</summary>
    public string? Status { get; set; }

    public string? FromWarehouse { get; set; }
    public string? ToWarehouse { get; set; }
}

public sealed class AddApprovedSapTransferRequestDto
{
    public string? ClientRequestId { get; set; }
}

public sealed class AddApprovedSapTransferResultDto
{
    public int DraftEntry { get; set; }
    public int DraftNum { get; set; }

    /// <summary>The draft's source warehouse, so a replay can be checked against the caller's warehouses.</summary>
    public string? FromWarehouse { get; set; }

    public string? ToWarehouse { get; set; }

    /// <summary>The transfer the add created; null when SAP did not say and it could not be recognised.</summary>
    public int? TransferDocEntry { get; set; }

    public int? TransferDocNum { get; set; }

    /// <summary>Whether <see cref="TransferDocEntry"/> was established.</summary>
    public bool Resolved { get; set; }

    /// <summary>The transfer requests the draft was copied from that this add closed in SAP.</summary>
    public List<int> ClosedRequestDocNums { get; set; } = [];

    /// <summary>Transfer requests the draft was copied from that are still open, each with why.</summary>
    public List<string> RequestsLeftOpen { get; set; } = [];

    public string Message { get; set; } = string.Empty;
}
