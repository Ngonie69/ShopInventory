namespace ShopInventory.DTOs;

/// <summary>
/// One SAP approval request against an A/R credit memo draft, as the approvals list shows it: the
/// request, the draft it holds, and what this app may do with it next.
/// </summary>
public sealed class CreditNoteApprovalListItemDto
{
    /// <summary>The SAP approval request code.</summary>
    public int Code { get; set; }

    /// <summary>Pending, Approved, NotApproved, Generated, GeneratedByAuthorizer or Cancelled.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The originator's remarks on the request, as typed in the B1 client.</summary>
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

    /// <summary>Without, Pending, Approved, Rejected, Generated, GeneratedbyAuthorizer or Cancelled — the draft's own state.</summary>
    public string? DraftAuthorizationStatus { get; set; }
    public bool DraftIsOpen { get; set; }
    public bool HasAttachment { get; set; }

    /// <summary>The credit note DocEntry once the request has been generated.</summary>
    public int? CreditNoteDocEntry { get; set; }

    /// <summary>The service approver may record a decision on the current stage.</summary>
    public bool CanDecide { get; set; }

    /// <summary>The request is approved and its draft is open, so it may be added.</summary>
    public bool CanAdd { get; set; }

    /// <summary>Why neither action is available, in a sentence — or what already happened.</summary>
    public string? StatusNote { get; set; }
}

public sealed class CreditNoteApprovalListResponseDto
{
    public List<CreditNoteApprovalListItemDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }

    /// <summary>The filter the list was read with: open, pending, approved or all.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Pass back as <c>beforeCode</c> to read the next page. Null when this page is the end of the
    /// queue.
    /// </summary>
    /// <remarks>
    /// Paging by this rather than by <see cref="Page"/> is stable while credit memos are still being
    /// raised: a new one takes the highest Code, so it lands above everything here and cannot push a
    /// row this reader has already seen onto their next page.
    /// </remarks>
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

    /// <summary>The full file name, <c>name.ext</c>.</summary>
    public string FileName { get; set; } = string.Empty;
    public string? Extension { get; set; }
    public DateTime? AttachedOn { get; set; }
    public string? FreeText { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>A PDF or an image, which the page can show inline; anything else is download-only.</summary>
    public bool IsViewable { get; set; }

    /// <summary>The API route that streams the bytes.</summary>
    public string DownloadUrl { get; set; } = string.Empty;
}

/// <summary>One approver's row on the request: who may decide at which stage, and what they decided.</summary>
public sealed class CreditNoteApprovalDecisionLineDto
{
    public int? StageCode { get; set; }
    public string? StageName { get; set; }
    public int? UserId { get; set; }
    public string? UserCode { get; set; }
    public string? UserName { get; set; }

    /// <summary>Pending, Approved or NotApproved.</summary>
    public string Status { get; set; } = string.Empty;
    public string? Remarks { get; set; }
    public DateTime? DecidedDate { get; set; }
    public string? DecidedTime { get; set; }
}

/// <summary>The stage SAP is waiting on, and where the service approver stands on it.</summary>
public sealed class CreditNoteApprovalStageDto
{
    public int? Code { get; set; }
    public string? Name { get; set; }
    public int? ApproversRequired { get; set; }
    public List<string> ApproverUserCodes { get; set; } = [];

    /// <summary>The SAP user this app records decisions as.</summary>
    public string ServiceApproverUserCode { get; set; } = string.Empty;
    public bool ServiceApproverListed { get; set; }
    public bool ServiceApproverAlreadyDecided { get; set; }
}

public sealed class CreditNoteApprovalDetailDto
{
    public CreditNoteApprovalListItemDto Request { get; set; } = new();
    public List<CreditNoteDraftLineDto> Lines { get; set; } = [];
    public List<CreditNoteDraftAttachmentDto> Attachments { get; set; } = [];

    /// <summary>The attachment record could not be read; the rest of the detail is still good.</summary>
    public bool AttachmentsUnavailable { get; set; }
    public string? AttachmentsMessage { get; set; }
    public List<CreditNoteApprovalDecisionLineDto> Decisions { get; set; } = [];
    public CreditNoteApprovalStageDto? Stage { get; set; }
}

public sealed class CreditNoteApprovalDecisionRequestDto
{
    /// <summary>Approved or NotApproved.</summary>
    public string Decision { get; set; } = string.Empty;
    public string? Remarks { get; set; }

    /// <summary>A caller-chosen key that makes a repeated submission replay the first answer.</summary>
    public string? ClientRequestId { get; set; }
}

public sealed class CreditNoteApprovalDecisionResultDto
{
    public int Code { get; set; }
    public string Decision { get; set; } = string.Empty;

    /// <summary>The request's status after SAP recorded the decision.</summary>
    public string Status { get; set; } = string.Empty;
    public bool CanAdd { get; set; }

    /// <summary>Approved at this stage, but SAP is still waiting on a later one.</summary>
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

    /// <summary>False when the draft was added but SAP's answer did not name the credit note it became.</summary>
    public bool Resolved { get; set; }
    public CreditNoteApprovalFiscalisationDto Fiscalisation { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}
