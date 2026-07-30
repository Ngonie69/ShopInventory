using System.Text.Json.Serialization;

namespace ShopInventory.Web.Models;

/// <summary>
/// A direct inventory transfer held while it moves through the approval process.
/// </summary>
public class PendingInventoryTransferDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>Reference for the draft while it has no SAP document number, e.g. DT-2026-00007.</summary>
    [JsonPropertyName("draftNumber")]
    public string? DraftNumber { get; set; }

    [JsonPropertyName("fromWarehouse")]
    public string? FromWarehouse { get; set; }

    [JsonPropertyName("toWarehouse")]
    public string? ToWarehouse { get; set; }

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

    [JsonPropertyName("lineCount")]
    public int LineCount { get; set; }

    [JsonPropertyName("totalQuantity")]
    public decimal TotalQuantity { get; set; }

    [JsonPropertyName("comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("docDate")]
    public DateTime? DocDate { get; set; }

    [JsonPropertyName("dueDate")]
    public DateTime? DueDate { get; set; }

    [JsonPropertyName("sapDocEntry")]
    public int? SapDocEntry { get; set; }

    [JsonPropertyName("sapDocNum")]
    public int? SapDocNum { get; set; }

    [JsonPropertyName("postedAtUtc")]
    public DateTime? PostedAtUtc { get; set; }

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

    [JsonPropertyName("lines")]
    public List<PendingInventoryTransferLineDto> Lines { get; set; } = [];
}

public class PendingInventoryTransferLineDto
{
    [JsonPropertyName("lineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("itemCode")]
    public string? ItemCode { get; set; }

    /// <summary>Blank for items the API's item cache has not seen; show the code on its own then.</summary>
    [JsonPropertyName("itemDescription")]
    public string? ItemDescription { get; set; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("uoMCode")]
    public string? UoMCode { get; set; }

    [JsonPropertyName("fromWarehouseCode")]
    public string? FromWarehouseCode { get; set; }

    [JsonPropertyName("toWarehouseCode")]
    public string? ToWarehouseCode { get; set; }
}

public class PendingInventoryTransferListResponse
{
    [JsonPropertyName("items")]
    public List<PendingInventoryTransferDto> Items { get; set; } = [];

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }
}

public class PendingInventoryTransferDecisionResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("pendingTransferId")]
    public Guid PendingTransferId { get; set; }

    [JsonPropertyName("decision")]
    public string? Decision { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("approvalProcessComplete")]
    public bool ApprovalProcessComplete { get; set; }

    [JsonPropertyName("transfer")]
    public InventoryTransferDto? Transfer { get; set; }
}

public class SubmitPendingTransferDecisionRequest
{
    [JsonPropertyName("decision")]
    public string Decision { get; set; } = string.Empty;

    [JsonPropertyName("stageId")]
    public Guid? StageId { get; set; }

    [JsonPropertyName("remarks")]
    public string? Remarks { get; set; }
}

/// <summary>Statuses a held transfer can be in.</summary>
public static class PendingTransferStatuses
{
    public const string AwaitingApproval = "AwaitingApproval";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Posted = "Posted";
    public const string PostFailed = "PostFailed";
    public const string Cancelled = "Cancelled";
}
