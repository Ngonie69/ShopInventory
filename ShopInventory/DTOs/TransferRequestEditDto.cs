using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

/// <summary>
/// A proposed change to an SAP transfer request: quantities may be adjusted, lines dropped and
/// either warehouse reassigned. The lines listed are what the request should be left with.
/// </summary>
public class EditTransferRequestDto
{
    [Required(ErrorMessage = "At least one line is required")]
    [MinLength(1, ErrorMessage = "At least one line is required")]
    public List<EditTransferRequestLineDto>? Lines { get; set; }

    /// <summary>
    /// The warehouse the stock should leave. Omit or leave blank to keep the request's own.
    /// A warehouse-scoped caller may only name a warehouse assigned to them.
    /// </summary>
    [MaxLength(50)]
    public string? FromWarehouse { get; set; }

    /// <summary>
    /// The warehouse the stock should arrive at. Omit or leave blank to keep the request's own.
    /// A warehouse-scoped caller may only name a warehouse assigned to them.
    /// </summary>
    [MaxLength(50)]
    public string? ToWarehouse { get; set; }

    /// <summary>Why the change is being made. Shown to the approver when the edit is held.</summary>
    public string? Reason { get; set; }
}

public class EditTransferRequestLineDto
{
    /// <summary>SAP LineNum of the line being kept. Lines omitted from the request are removed.</summary>
    public int LineNum { get; set; }

    [Range(0.00001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }
}

/// <summary>
/// A line on a held edit, carrying enough detail to review it without re-reading SAP.
/// </summary>
public class TransferRequestEditLineDto
{
    public int LineNum { get; set; }
    public string? ItemCode { get; set; }
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public string? UoMCode { get; set; }
}

/// <summary>
/// Result of proposing a change: either written straight to SAP, or held for approval.
/// </summary>
public class TransferRequestEditResponseDto
{
    public string Message { get; set; } = string.Empty;
    public int RequestDocEntry { get; set; }

    /// <summary>True when the change was held rather than written to SAP.</summary>
    public bool RequiresApproval { get; set; }

    /// <summary>The updated SAP request, when the change was applied directly.</summary>
    public InventoryTransferRequestDto? TransferRequest { get; set; }

    /// <summary>The held change, when approval is required.</summary>
    public PendingTransferRequestEditDto? PendingEdit { get; set; }
}

/// <summary>
/// A change to a transfer request that is waiting on approval before it reaches SAP.
/// </summary>
public class PendingTransferRequestEditDto
{
    public Guid Id { get; set; }
    public int RequestDocEntry { get; set; }
    public int RequestDocNum { get; set; }

    /// <summary>The request's source warehouse as it stood when the change was proposed.</summary>
    public string? FromWarehouse { get; set; }

    /// <summary>The request's destination warehouse as it stood when the change was proposed.</summary>
    public string? ToWarehouse { get; set; }

    /// <summary>The source warehouse the change would move the request to, when it reassigns one.</summary>
    public string? ProposedFromWarehouse { get; set; }

    /// <summary>The destination warehouse the change would move the request to, when it reassigns one.</summary>
    public string? ProposedToWarehouse { get; set; }

    /// <summary>AwaitingApproval, Rejected, Applied, ApplyFailed or Cancelled.</summary>
    public string Status { get; set; } = string.Empty;

    public string CreatedByName { get; set; } = string.Empty;
    public string? CreatedByRole { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public DateTime? AppliedAtUtc { get; set; }
    public string? Reason { get; set; }
    public string? LastError { get; set; }

    public Guid? ApprovalRequestId { get; set; }
    public string? ApprovalTemplateName { get; set; }
    public string? ApprovalStatus { get; set; }
    public List<ApprovalStageProgressDto> ApprovalStages { get; set; } = [];

    /// <summary>Whether the caller may record a decision on this change right now.</summary>
    public bool CanCurrentUserDecide { get; set; }

    public List<TransferRequestEditLineDto> OriginalLines { get; set; } = [];
    public List<TransferRequestEditLineDto> ProposedLines { get; set; } = [];
}

public class PendingTransferRequestEditListResponseDto
{
    public List<PendingTransferRequestEditDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class PendingTransferRequestEditDecisionResponseDto
{
    public string Message { get; set; } = string.Empty;
    public Guid PendingEditId { get; set; }
    public string Decision { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool ApprovalProcessComplete { get; set; }

    /// <summary>The updated SAP request once the final approval applies the change.</summary>
    public InventoryTransferRequestDto? TransferRequest { get; set; }
}
