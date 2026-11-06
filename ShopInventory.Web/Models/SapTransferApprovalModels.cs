namespace ShopInventory.Web.Models;

// Mirrors of ShopInventory/DTOs/SapTransferApprovalDto.cs. Nullability matches the API's on every value
// type: a nullable int there read into a plain int here throws, and the page reads as "no data".

public sealed class SapTransferApprovalListItemDto
{
    public int DraftEntry { get; set; }
    public int DraftNum { get; set; }
    public DateTime? DocDate { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? FromWarehouse { get; set; }
    public string? ToWarehouse { get; set; }
    public string? Comments { get; set; }
    public string DraftStatus { get; set; } = string.Empty;
    public bool HasAttachment { get; set; }
    public int? RequestCode { get; set; }
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
    public bool CanAdd { get; set; }
    public string? StatusNote { get; set; }
}

public sealed class SapTransferApprovalListResponseDto
{
    public List<SapTransferApprovalListItemDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public string? Warehouse { get; set; }
    public List<string>? ScopedWarehouses { get; set; }
    public int? NextCursor { get; set; }
}

public sealed class SapTransferApprovalDetailDto
{
    public SapTransferApprovalListItemDto Draft { get; set; } = new();
    public List<SapTransferDraftLineDto> Lines { get; set; } = [];
    public List<SapTransferApprovalRequestDto> Approvals { get; set; } = [];
    public List<SapTransferLinkedRequestDto> LinkedRequests { get; set; } = [];
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
    public bool? ManagesBatches { get; set; }
    public bool? ManagesSerials { get; set; }
    public List<SapTransferDraftBatchDto> Batches { get; set; } = [];
    public int SerialCount { get; set; }
    public bool SelectionMissing { get; set; }
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

public sealed class SapTransferLinkedRequestDto
{
    public int DocEntry { get; set; }
    public int? DocNum { get; set; }
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
    public string? FromWarehouse { get; set; }
    public string? ToWarehouse { get; set; }
    public int? TransferDocEntry { get; set; }
    public int? TransferDocNum { get; set; }
    public bool Resolved { get; set; }
    public List<int> ClosedRequestDocNums { get; set; } = [];
    public List<string> RequestsLeftOpen { get; set; } = [];
    public string Message { get; set; } = string.Empty;
}
