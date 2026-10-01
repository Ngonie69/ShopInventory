namespace ShopInventory.Web.Models;

/// <summary>
/// The ADR performance report, mirroring the API's <c>AdrPerformanceReportResult</c>.
/// </summary>
/// <remarks>
/// Hand-mirrored like every API DTO here, so nullability must match: a share is null when the vans
/// took nothing in that currency, and declaring it non-nullable would make the whole read fail and
/// the page say "no data". Computed properties are re-derived because they mirror the API's.
/// </remarks>
public class AdrPerformanceReportResponse
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public AdrPerformanceOverall Overall { get; set; } = new();
    public List<AdrPerformanceRow> Adrs { get; set; } = [];
}

public class AdrPerformanceOverall
{
    public int AdrCount { get; set; }
    public int ActiveAdrCount { get; set; }
    public AdrOrderCounts OrderCounts { get; set; } = new();
    public List<AdrContribution> Orders { get; set; } = [];
    public List<AdrContribution> Sales { get; set; } = [];
}

public class AdrOrderCounts
{
    public int Total { get; set; }
    public int InSap { get; set; }
    public int Fulfilled { get; set; }
    public int Pending { get; set; }
    public int Cancelled { get; set; }
}

/// <summary>One currency's ADR figure against the same figure for every van rep.</summary>
public class AdrContribution
{
    public string Currency { get; set; } = string.Empty;
    public int AdrDocumentCount { get; set; }
    public decimal AdrGross { get; set; }
    public int VanDocumentCount { get; set; }
    public decimal VanGross { get; set; }

    public double? Share => VanGross > 0 ? (double)(AdrGross / VanGross) : null;
}

public class AdrPerformanceRow
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string? VanAccountCode { get; set; }
    public bool IsActive { get; set; }
    public int ActiveDays { get; set; }
    public AdrOrderCounts OrderCounts { get; set; } = new();
    public int OrderCustomerCount { get; set; }
    public List<VanSalesMoney> OrderTotalsByCurrency { get; set; } = [];
    public int SaleCustomerCount { get; set; }
    public List<VanSalesMoney> SalesTotalsByCurrency { get; set; } = [];
    public List<AdrShare> Shares { get; set; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;
}

/// <summary>One currency's share of every van's trade; null when the vans took nothing in it.</summary>
public class AdrShare
{
    public string Currency { get; set; } = string.Empty;
    public double? OrderShare { get; set; }
    public double? SalesShare { get; set; }
}
