namespace ShopInventory.DTOs;

/// <summary>One breakage report as a row in a list.</summary>
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

/// <summary>A page of breakage reports, with how many sit in each status.</summary>
public sealed class MarketBreakageListResponseDto
{
    public List<MarketBreakageSummaryDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }

    /// <summary>Every status with its count, filters aside, so the tabs can show them.</summary>
    public Dictionary<string, int> StatusCounts { get; set; } = [];
}

/// <summary>One breakage report with its lines and what happened to it.</summary>
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

/// <summary>The office's count for each line, and the go-ahead to transfer it to returns.</summary>
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

/// <summary>What a confirm or reject did, with the report as it now stands.</summary>
public sealed class MarketBreakageDecisionResultDto
{
    public string Message { get; set; } = string.Empty;
    public MarketBreakageDetailDto Breakage { get; set; } = new();
}

/// <summary>What the handset is told when it reports a breakage.</summary>
public sealed class VanSalesMarketBreakageResponse
{
    public int Id { get; set; }
    public string Status { get; set; } = string.Empty;

    /// <summary>True when this was a resend of a report the server already had.</summary>
    public bool AlreadyReported { get; set; }
    public string Message { get; set; } = string.Empty;
}
