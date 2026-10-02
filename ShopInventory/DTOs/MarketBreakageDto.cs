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

/// <summary>
/// Every report a view of the office list holds, with its lines: what the Excel and PDF exports are
/// built from. Newest first, as the list orders them.
/// </summary>
public sealed class MarketBreakageExportDto
{
    /// <summary>The status filter the list had: open, a status, or null for all.</summary>
    public string? Status { get; set; }

    public string? Search { get; set; }

    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>How many reports matched; more than <see cref="Reports"/> holds when <see cref="Truncated"/>.</summary>
    public int TotalCount { get; set; }

    /// <summary>The match ran past the export's cap, so only the newest reports are here.</summary>
    public bool Truncated { get; set; }

    public MarketBreakageExportTotalsDto Totals { get; set; } = new();

    /// <summary>Per van, most units reported first.</summary>
    public List<MarketBreakageExportGroupDto> ByVan { get; set; } = [];

    /// <summary>Per product, most units reported first.</summary>
    public List<MarketBreakageExportGroupDto> ByProduct { get; set; } = [];

    public List<MarketBreakageDetailDto> Reports { get; set; } = [];
}

/// <summary>
/// The export's figures. Counted is what the office confirmed, so a report not yet counted adds to
/// Reported only; Transferred is the counted stock of reports SAP has taken into returns.
/// </summary>
public sealed class MarketBreakageExportTotalsDto
{
    public int Reports { get; set; }

    /// <summary>Pending, failed or stranded: still the office's to finish.</summary>
    public int OpenReports { get; set; }

    public int Lines { get; set; }
    public decimal ReportedQuantity { get; set; }
    public decimal CountedQuantity { get; set; }
    public decimal TransferredQuantity { get; set; }

    /// <summary>What the reps reported on reports the office turned down.</summary>
    public decimal RejectedQuantity { get; set; }
}

/// <summary>One van's or one product's share of an export.</summary>
public sealed class MarketBreakageExportGroupDto
{
    /// <summary>The van's warehouse code, or the item code.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>The reps who reported on the van, or the item's description.</summary>
    public string? Name { get; set; }

    public int Reports { get; set; }
    public decimal ReportedQuantity { get; set; }
    public decimal CountedQuantity { get; set; }
    public decimal TransferredQuantity { get; set; }
}
