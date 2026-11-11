namespace ShopInventory.Web.Models;

// Mirrors of ShopInventory/DTOs/SapTransferApprovalDto.cs. Nullability matches the API's on every value
// type: a nullable int there read into a plain int here throws, and the page reads as "no data".

public sealed class SapTransferApprovalListItemDto
{
    public int DraftEntry { get; set; }

    /// <summary><c>Transfer</c>, which moves stock when added, or <c>Request</c>, which does not.</summary>
    public string Kind { get; set; } = SapHeldDraftKinds.Transfer;

    public bool IsRequest => Kind == SapHeldDraftKinds.Request;
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

/// <summary>The two kinds of draft the held-transfers page reads, as the API names them.</summary>
public static class SapHeldDraftKinds
{
    public const string Transfer = "Transfer";
    public const string Request = "Request";
}

/// <summary>
/// The page's filters, sent as the list route's query parameters (<c>kind</c>, <c>warehouse</c>, repeated
/// <c>from</c>/<c>to</c>, <c>createdFrom</c>/<c>createdTo</c>, <c>attachedOnly</c>, <c>draftNum</c>).
/// Every one is applied in SAP, so the count that comes back is of the filtered queue.
/// </summary>
public sealed record SapTransferQueueFilter
{
    /// <summary><see cref="SapHeldDraftKinds.Transfer"/>, <see cref="SapHeldDraftKinds.Request"/>, or null for both.</summary>
    public string? Kind { get; init; }

    /// <summary>Leaving or entering this warehouse — the search box's text when it is not a number.</summary>
    public string? Warehouse { get; init; }

    public IReadOnlyList<string> FromWarehouses { get; init; } = [];
    public IReadOnlyList<string> ToWarehouses { get; init; } = [];
    public DateTime? CreatedFrom { get; init; }
    public DateTime? CreatedTo { get; init; }
    public bool AttachedOnly { get; init; }

    /// <summary>One draft, by the number the B1 client shows — the search box's text when it is one.</summary>
    public int? DraftNum { get; init; }

    public string ToQueryString()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Kind))
        {
            parts.Add($"kind={Uri.EscapeDataString(Kind)}");
        }

        if (!string.IsNullOrWhiteSpace(Warehouse))
        {
            parts.Add($"warehouse={Uri.EscapeDataString(Warehouse.Trim())}");
        }

        parts.AddRange(FromWarehouses.Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => $"from={Uri.EscapeDataString(code.Trim())}"));
        parts.AddRange(ToWarehouses.Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => $"to={Uri.EscapeDataString(code.Trim())}"));

        if (CreatedFrom is DateTime from)
        {
            parts.Add($"createdFrom={from:yyyy-MM-dd}");
        }

        if (CreatedTo is DateTime to)
        {
            parts.Add($"createdTo={to:yyyy-MM-dd}");
        }

        if (AttachedOnly)
        {
            parts.Add("attachedOnly=true");
        }

        if (DraftNum is int draftNum)
        {
            parts.Add($"draftNum={draftNum}");
        }

        return parts.Count == 0 ? string.Empty : "&" + string.Join('&', parts);
    }
}

public sealed class SapTransferApprovalListResponseDto
{
    public List<SapTransferApprovalListItemDto> Items { get; set; } = [];
    public int TotalCount { get; set; }

    /// <summary>Both kinds' totals for the same search and filters; null when SAP could not count one.</summary>
    public int? TransferCount { get; set; }

    public int? RequestCount { get; set; }

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
    public string Kind { get; set; } = SapHeldDraftKinds.Transfer;
    public string? FromWarehouse { get; set; }
    public string? ToWarehouse { get; set; }
    public int? TransferDocEntry { get; set; }
    public int? TransferDocNum { get; set; }
    public int? CreatedRequestDocEntry { get; set; }
    public int? CreatedRequestDocNum { get; set; }
    public bool Resolved { get; set; }
    public List<int> ClosedRequestDocNums { get; set; } = [];
    public List<string> RequestsLeftOpen { get; set; } = [];
    public string Message { get; set; } = string.Empty;
}
