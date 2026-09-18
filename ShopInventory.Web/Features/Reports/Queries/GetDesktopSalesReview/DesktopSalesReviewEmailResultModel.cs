// Generated from ShopInventory/Features/DesktopIntegration/Commands/SendDesktopSalesReviewEmail/DesktopSalesReviewEmailResult.cs by mirror_records.py: the API records, as the Web mirrors them.
// Nullability matches the API exactly; regenerate rather than hand-edit.

namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;

public sealed class DesktopSalesReviewEmailResult
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
