namespace ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

/// <summary>
/// Links from the hub into the full reports on /reports, carrying the period so the report opens
/// on the same dates the hub was showing.
/// </summary>
public static class ReportsHubLinks
{
    public const string Profit = "profit";
    public const string Sales = "sales";
    public const string Products = "products";
    public const string Stock = "stock";
    public const string Payments = "payments";
    public const string Customers = "customers";
    public const string CreditNotes = "creditnotes";
    public const string Purchasing = "purchasing";
    public const string Aging = "aging";
    public const string LowStock = "alerts";
    public const string SlowMoving = "slowmoving";
    public const string Fulfillment = "fulfillment";

    /// <summary>The tabs /reports can open on; anything else shows the hub.</summary>
    public static readonly IReadOnlySet<string> Tabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Profit, Sales, Products, Stock, Payments, Customers, CreditNotes, Purchasing, Aging, LowStock, SlowMoving, Fulfillment
    };

    public static string Tab(string tab, ReportsPeriod period) => $"/reports?tab={tab}&{period.Query}";

    public static string Hub(ReportsPeriod period) => $"/reports?{period.Query}";
}
