// Mirrors ShopInventory/Features/ManagementSales/Commands/SendSalesPerformanceEmail/SalesPerformanceEmailResult.cs.
// Nullability matches the API exactly.

namespace ShopInventory.Web.Features.Reports.Queries.GetSalesPerformanceEmailSchedule;

public sealed class SalesPerformanceEmailResult
{
    public string Cadence { get; set; } = string.Empty;
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public List<string> SentTo { get; set; } = [];
    public List<string> Failed { get; set; } = [];
    public bool Skipped { get; set; }
    public string? Reason { get; set; }
    public int FindingCount { get; set; }
}
