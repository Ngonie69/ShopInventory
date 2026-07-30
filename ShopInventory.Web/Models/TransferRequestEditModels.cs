using System.Text.Json.Serialization;

namespace ShopInventory.Web.Models;

/// <summary>
/// A proposed change to an SAP transfer request: the lines it should be left with.
/// </summary>
public class EditTransferRequestRequest
{
    [JsonPropertyName("lines")]
    public List<EditTransferRequestLineRequest> Lines { get; set; } = [];

    /// <summary>The source warehouse to move the request onto. Null leaves the request's own.</summary>
    [JsonPropertyName("fromWarehouse")]
    public string? FromWarehouse { get; set; }

    /// <summary>The destination warehouse to move the request onto. Null leaves the request's own.</summary>
    [JsonPropertyName("toWarehouse")]
    public string? ToWarehouse { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

public class EditTransferRequestLineRequest
{
    [JsonPropertyName("lineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }
}

public class TransferRequestEditLineDto
{
    [JsonPropertyName("lineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("itemCode")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("itemDescription")]
    public string? ItemDescription { get; set; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("uoMCode")]
    public string? UoMCode { get; set; }
}

public class TransferRequestEditResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("requestDocEntry")]
    public int RequestDocEntry { get; set; }

    [JsonPropertyName("requiresApproval")]
    public bool RequiresApproval { get; set; }

    [JsonPropertyName("transferRequest")]
    public InventoryTransferRequestDto? TransferRequest { get; set; }

    [JsonPropertyName("pendingEdit")]
    public PendingTransferRequestEditDto? PendingEdit { get; set; }
}

/// <summary>
/// A change to a transfer request waiting on approval before it reaches SAP.
/// </summary>
public class PendingTransferRequestEditDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("requestDocEntry")]
    public int RequestDocEntry { get; set; }

    [JsonPropertyName("requestDocNum")]
    public int RequestDocNum { get; set; }

    [JsonPropertyName("fromWarehouse")]
    public string? FromWarehouse { get; set; }

    [JsonPropertyName("toWarehouse")]
    public string? ToWarehouse { get; set; }

    /// <summary>Set when the change reassigns the source warehouse.</summary>
    [JsonPropertyName("proposedFromWarehouse")]
    public string? ProposedFromWarehouse { get; set; }

    /// <summary>Set when the change reassigns the destination warehouse.</summary>
    [JsonPropertyName("proposedToWarehouse")]
    public string? ProposedToWarehouse { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("createdByName")]
    public string CreatedByName { get; set; } = string.Empty;

    [JsonPropertyName("createdByRole")]
    public string? CreatedByRole { get; set; }

    [JsonPropertyName("createdByUserId")]
    public Guid CreatedByUserId { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; }

    [JsonPropertyName("decidedAtUtc")]
    public DateTime? DecidedAtUtc { get; set; }

    [JsonPropertyName("appliedAtUtc")]
    public DateTime? AppliedAtUtc { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    [JsonPropertyName("approvalRequestId")]
    public Guid? ApprovalRequestId { get; set; }

    [JsonPropertyName("approvalTemplateName")]
    public string? ApprovalTemplateName { get; set; }

    [JsonPropertyName("approvalStatus")]
    public string? ApprovalStatus { get; set; }

    [JsonPropertyName("approvalStages")]
    public List<ApprovalStageProgressModel> ApprovalStages { get; set; } = [];

    [JsonPropertyName("canCurrentUserDecide")]
    public bool CanCurrentUserDecide { get; set; }

    [JsonPropertyName("originalLines")]
    public List<TransferRequestEditLineDto> OriginalLines { get; set; } = [];

    [JsonPropertyName("proposedLines")]
    public List<TransferRequestEditLineDto> ProposedLines { get; set; } = [];
}

public class PendingTransferRequestEditListResponse
{
    [JsonPropertyName("items")]
    public List<PendingTransferRequestEditDto> Items { get; set; } = [];

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

public class PendingTransferRequestEditDecisionResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("pendingEditId")]
    public Guid PendingEditId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("approvalProcessComplete")]
    public bool ApprovalProcessComplete { get; set; }

    [JsonPropertyName("transferRequest")]
    public InventoryTransferRequestDto? TransferRequest { get; set; }
}

public static class PendingRequestEditStatuses
{
    public const string AwaitingApproval = "AwaitingApproval";
    public const string Rejected = "Rejected";
    public const string Applied = "Applied";
    public const string ApplyFailed = "ApplyFailed";
    public const string Cancelled = "Cancelled";
}
