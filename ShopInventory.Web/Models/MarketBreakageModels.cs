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
