using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

/// <summary>What happened in the period: daily sales, who bought, what sold, how it was paid and what is still open.</summary>
public sealed record ReportsActivityResult(
    SalesSummaryReport? DailySales,
    TopCustomersReport? TopCustomers,
    TopProductsReport? TopProducts,
    PaymentSummaryReport? Payments,
    OrderFulfillmentReport? Orders,
    CreditNoteSummaryReport? CreditNotes,
    IReadOnlyCollection<string> Unavailable);
