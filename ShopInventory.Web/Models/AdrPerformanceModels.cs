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
    public List<AdrChannel> OrdersByChannel { get; set; } = [];
    public AdrPerformanceDetail? Detail { get; set; }
    public List<string> Caveats { get; set; } = [];
}

public class AdrPerformanceOverall
{
    public int AdrCount { get; set; }
    public int ActiveAdrCount { get; set; }
    public AdrOrderCounts OrderCounts { get; set; } = new();
    public List<AdrContribution> Orders { get; set; } = [];
    public List<AdrContribution> Sales { get; set; } = [];
    public int? Calls { get; set; }
    public int ProductiveCalls { get; set; }

    public double? StrikeRate => Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;
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
    public int? Calls { get; set; }
    public int ProductiveCalls { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;

    public double? StrikeRate => Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;
}

/// <summary>One currency's share of every van's trade; null when the vans took nothing in it.</summary>
public class AdrShare
{
    public string Currency { get; set; } = string.Empty;
    public double? OrderShare { get; set; }
    public double? SalesShare { get; set; }
}

/// <summary>One group's part of every sales order raised in the period, whatever its source.</summary>
public class AdrChannel
{
    public string Channel { get; set; } = string.Empty;
    public bool IsAdr { get; set; }
    public int OrderCount { get; set; }
    public int PeopleCount { get; set; }
    public List<AdrChannelShare> Shares { get; set; } = [];
}

public class AdrChannelShare
{
    public string Currency { get; set; } = string.Empty;
    public decimal Gross { get; set; }
    public double? Share { get; set; }
}

/// <summary>One ADR's shops, the items they ordered and their orders, present only for one ADR.</summary>
public class AdrPerformanceDetail
{
    public Guid UserId { get; set; }
    public List<AdrShop> Shops { get; set; } = [];
    public List<AdrItem> Items { get; set; } = [];
    public List<AdrOrder> Orders { get; set; } = [];
    public int OrdersNotListed { get; set; }
}

public class AdrShop
{
    public string CustomerCode { get; set; } = string.Empty;
    public string? CustomerName { get; set; }
    public int OrderCount { get; set; }
    public List<VanSalesMoney> OrderTotalsByCurrency { get; set; } = [];
    public int SaleCount { get; set; }
    public List<VanSalesMoney> SalesTotalsByCurrency { get; set; } = [];
    public DateTime LastActiveOn { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(CustomerName) ? CustomerCode : CustomerName;
}

/// <summary>An item the ADR ordered. Value is line totals, a different measure from order totals.</summary>
public class AdrItem
{
    public int Rank { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public int LineCount { get; set; }
    public int ShopCount { get; set; }
    public List<VanSalesQuantity> QuantitiesByUoM { get; set; } = [];
    public List<VanSalesLineMoney> TotalsByCurrency { get; set; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(ItemDescription) ? ItemCode : ItemDescription;
}

public class AdrOrder
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public int? SapDocNum { get; set; }
    public DateTime TradingDate { get; set; }
    public string? CustomerCode { get; set; }
    public string? CustomerName { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public decimal DocTotal { get; set; }
    public int LineCount { get; set; }

    public string DisplayCustomer =>
        !string.IsNullOrWhiteSpace(CustomerName) ? CustomerName
        : !string.IsNullOrWhiteSpace(CustomerCode) ? CustomerCode
        : "No shop recorded";
}
