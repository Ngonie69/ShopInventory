namespace ShopInventory.Web.Models;

// Hand-mirrors of the API's CreditNoteApprovalDto.cs. Nullability must match the API's: a non-null
// property here for a null the API sends makes System.Text.Json throw, and the page reports "no data".

public sealed class CreditNoteApprovalListItemDto
{
    public int Code { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Remarks { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedTime { get; set; }
    public int? TemplateCode { get; set; }
    public string? TemplateName { get; set; }
    public int? StageCode { get; set; }
    public string? StageName { get; set; }
    public int? OriginatorId { get; set; }
    public string? OriginatorUserCode { get; set; }
    public string? OriginatorName { get; set; }
    public int? DraftEntry { get; set; }
    public int? DraftNum { get; set; }
    public string? CardCode { get; set; }
    public string? CardName { get; set; }
    public DateTime? DocDate { get; set; }
    public decimal DocTotal { get; set; }
    public decimal VatSum { get; set; }
    public string? DocCurrency { get; set; }
    public string? NumAtCard { get; set; }
    public string? Comments { get; set; }
    public string? DraftAuthorizationStatus { get; set; }
    public bool DraftIsOpen { get; set; }
    public bool HasAttachment { get; set; }
    public int? CreditNoteDocEntry { get; set; }
    public bool CanDecide { get; set; }
    public bool CanAdd { get; set; }
    public string? StatusNote { get; set; }
}

public sealed class CreditNoteApprovalListResponseDto
{
    public List<CreditNoteApprovalListItemDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public string Status { get; set; } = string.Empty;

    /// <summary>Pass back as <c>beforeCode</c> for the next page; null at the end of the queue.</summary>
    public int? NextCursor { get; set; }
}

public sealed class CreditNoteDraftLineDto
{
    public int LineNum { get; set; }
    public string? ItemCode { get; set; }
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public decimal VatSum { get; set; }
    public string? WarehouseCode { get; set; }
    public string? TaxCode { get; set; }
    public int? BaseType { get; set; }
    public int? BaseEntry { get; set; }
    public int? BaseLine { get; set; }
    public string? CreditReason { get; set; }
}

public sealed class CreditNoteDraftAttachmentDto
{
    public int LineNum { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? Extension { get; set; }
    public DateTime? AttachedOn { get; set; }
    public string? FreeText { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
    public bool IsViewable { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
}

public sealed class CreditNoteApprovalDecisionLineDto
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

public sealed class CreditNoteApprovalStageDto
{
    public int? Code { get; set; }
    public string? Name { get; set; }
    public int? ApproversRequired { get; set; }
    public List<string> ApproverUserCodes { get; set; } = [];
    public string ServiceApproverUserCode { get; set; } = string.Empty;
    public bool ServiceApproverListed { get; set; }
    public bool ServiceApproverAlreadyDecided { get; set; }
}

public sealed class CreditNoteApprovalDetailDto
{
    public CreditNoteApprovalListItemDto Request { get; set; } = new();
    public List<CreditNoteDraftLineDto> Lines { get; set; } = [];
    public List<CreditNoteDraftAttachmentDto> Attachments { get; set; } = [];
    public bool AttachmentsUnavailable { get; set; }
    public string? AttachmentsMessage { get; set; }
    public List<CreditNoteApprovalDecisionLineDto> Decisions { get; set; } = [];
    public CreditNoteApprovalStageDto? Stage { get; set; }
}

public sealed class CreditNoteApprovalDecisionRequestDto
{
    public string Decision { get; set; } = string.Empty;
    public string? Remarks { get; set; }
    public string? ClientRequestId { get; set; }
}

public sealed class CreditNoteApprovalDecisionResultDto
{
    public int Code { get; set; }
    public string Decision { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool CanAdd { get; set; }
    public bool StillPending { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class AddApprovedCreditNoteRequestDto
{
    public string? ClientRequestId { get; set; }
}

public sealed class CreditNoteApprovalFiscalisationDto
{
    public bool Attempted { get; set; }
    public bool Success { get; set; }
    public bool Skipped { get; set; }
    public string? Message { get; set; }
    public string? ReceiptGlobalNo { get; set; }
}

public sealed class AddApprovedCreditNoteResultDto
{
    public int Code { get; set; }
    public int DraftEntry { get; set; }
    public int? CreditNoteDocEntry { get; set; }
    public int? CreditNoteDocNum { get; set; }
    public bool Resolved { get; set; }
    public CreditNoteApprovalFiscalisationDto Fiscalisation { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}
