namespace ShopInventory.Web.Models;

// Mirrors ShopInventory/DTOs/MarketBreakageDto.cs by hand. Nullability must match the API's: a value
// the API may send as null, declared non-null here, fails the whole read as "could not be loaded".

/// <summary>The states a breakage report moves through, as the API spells them.</summary>
public static class MarketBreakageStatus
{
    public const string Open = "open";
    public const string Pending = "Pending";
    public const string Transferring = "Transferring";
    public const string Transferred = "Transferred";
    public const string TransferFailed = "TransferFailed";
    public const string Rejected = "Rejected";

    /// <summary>The office may confirm (or retry) from these.</summary>
    public static bool MayConfirm(string status) => status is Pending or TransferFailed or Transferring;

    /// <summary>
    /// The office may reject from these. Not Transferring: that transfer may already be in SAP.
    /// </summary>
    public static bool MayReject(string status) => status is Pending or TransferFailed;

    public static string Describe(string status) => status switch
    {
        Pending => "Pending",
        Transferring => "Transferring",
        Transferred => "Transferred",
        TransferFailed => "Transfer failed",
        Rejected => "Rejected",
        _ => status
    };

    /// <summary>
    /// What an export calls the list's status filter — the same words the API's PDF uses, so the
    /// Excel and the PDF of one view carry one name.
    /// </summary>
    public static string ScopeLabel(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return "All reports";
        if (string.Equals(filter, Open, StringComparison.OrdinalIgnoreCase))
            return "Waiting on the office";
        return string.Equals(filter, Pending, StringComparison.OrdinalIgnoreCase) ? "To count" : Describe(filter);
    }
}

public sealed class MarketBreakageSummaryDto
{
    public int Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public string ReportedByName { get; set; } = string.Empty;
    public string VanWarehouseCode { get; set; } = string.Empty;
    public string? CardCode { get; set; }
    public string? CardName { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int LineCount { get; set; }
    public decimal TotalReportedQuantity { get; set; }
    public decimal? TotalConfirmedQuantity { get; set; }
    public int? SapDocNum { get; set; }
}

public sealed class MarketBreakageListResponseDto
{
    public List<MarketBreakageSummaryDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public Dictionary<string, int> StatusCounts { get; set; } = [];
}

public sealed class MarketBreakageDetailDto
{
    public int Id { get; set; }
    public string ClientRequestId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public Guid ReportedByUserId { get; set; }
    public string ReportedByName { get; set; } = string.Empty;
    public string VanWarehouseCode { get; set; } = string.Empty;
    public string? CardCode { get; set; }
    public string? CardName { get; set; }
    public string? Remarks { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? DecidedByName { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionRemarks { get; set; }
    public string? ReturnsWarehouseCode { get; set; }
    public int? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }
    public DateTime? TransferredAtUtc { get; set; }
    public DateTime? LastAttemptedAtUtc { get; set; }
    public string? LastError { get; set; }
    public List<MarketBreakageLineDto> Lines { get; set; } = [];
}

public sealed class MarketBreakageLineDto
{
    public int Id { get; set; }
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public string? Reason { get; set; }
    public decimal ReportedQuantity { get; set; }
    public decimal? ConfirmedQuantity { get; set; }
}

/// <summary>Every report a view of the list holds, with the lines and the figures: what the exports are built from.</summary>
public sealed class MarketBreakageExportDto
{
    public string? Status { get; set; }
    public string? Search { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public int TotalCount { get; set; }
    public bool Truncated { get; set; }
    public MarketBreakageExportTotalsDto Totals { get; set; } = new();
    public List<MarketBreakageExportGroupDto> ByVan { get; set; } = [];
    public List<MarketBreakageExportGroupDto> ByProduct { get; set; } = [];
    public List<MarketBreakageDetailDto> Reports { get; set; } = [];
}

public sealed class MarketBreakageExportTotalsDto
{
    public int Reports { get; set; }
    public int OpenReports { get; set; }
    public int Lines { get; set; }
    public decimal ReportedQuantity { get; set; }
    public decimal CountedQuantity { get; set; }
    public decimal TransferredQuantity { get; set; }
    public decimal RejectedQuantity { get; set; }
}

public sealed class MarketBreakageExportGroupDto
{
    public string Code { get; set; } = string.Empty;
    public string? Name { get; set; }
    public int Reports { get; set; }
    public decimal ReportedQuantity { get; set; }
    public decimal CountedQuantity { get; set; }
    public decimal TransferredQuantity { get; set; }
}

public sealed class ConfirmMarketBreakageRequestDto
{
    public List<ConfirmMarketBreakageLineDto> Lines { get; set; } = [];
    public string? Remarks { get; set; }
}

public sealed class ConfirmMarketBreakageLineDto
{
    public int LineId { get; set; }
    public decimal ConfirmedQuantity { get; set; }
}

public sealed class RejectMarketBreakageRequestDto
{
    public string? Remarks { get; set; }
}

public sealed class MarketBreakageDecisionResultDto
{
    public string Message { get; set; } = string.Empty;
    public MarketBreakageDetailDto Breakage { get; set; } = new();
}
