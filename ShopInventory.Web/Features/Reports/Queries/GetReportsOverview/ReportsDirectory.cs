using System.Security.Claims;
using ShopInventory.Web.Data;

namespace ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

/// <summary>
/// Every report a user can open, grouped, so nobody has to hunt the sidebar for one.
/// </summary>
/// <remarks>
/// The roles on each entry are the ones NavMenu.razor wraps that report's link in, so the hub never
/// offers a report the sidebar would hide. A report reached through a tab of /reports carries the
/// hub's own roles, since that page is what gates it.
/// </remarks>
public static class ReportsDirectory
{
    private const string VanReportRoles = "Admin,Manager";
    private const string ShopSalesRoles = "Admin,Manager,Cashier";
    private const string RouteSalesRoles = "Admin,Manager,Cashier";
    private const string PartnerSalesRoles = "Admin,SalesRep";

    public static IReadOnlyList<ReportsDirectoryGroup> All(ReportsPeriod period)
    {
        string Tab(string tab) => ReportsHubLinks.Tab(tab, period);
        const string hub = UserRoles.InsightsRoles;

        return
        [
            new("Sales",
            [
                new("Management Sales", "Shops, vending and vans side by side", "/reports/management-sales", ShopSalesRoles),
                new("Shop & Vending Analysis", "Till and vending sales by product and channel", "/reports/desktop-sales", ShopSalesRoles),
                new("Shop & Vending Review", "A day-by-day review of shop takings", "/reports/desktop-sales/review", ShopSalesRoles),
                new("Sales summary", "Invoiced sales, VAT and margin by day", Tab(ReportsHubLinks.Sales), hub),
                new("Sales Order vs Invoice", "What was ordered against what was invoiced", "/reports/order-fulfillment", UserRoles.OrderFulfillmentReportRoles),
                new("Customer Revenue", "Sales per customer for any period", "/reports/customer-revenue", PartnerSalesRoles),
                new("Partner Performance", "How each customer is trending", "/reports/partner-performance", PartnerSalesRoles),
                new("Item Volume", "Units sold per product", "/reports/item-volume", PartnerSalesRoles),
            ]),
            new("Money",
            [
                new("Profit overview", "Revenue, cost of sales, VAT and margin", Tab(ReportsHubLinks.Profit), hub),
                new("Payments received", "Collections by method and by day", Tab(ReportsHubLinks.Payments), hub),
                new("Receivables aging", "Who owes what, and for how long", Tab(ReportsHubLinks.Aging), hub),
                new("Credit notes", "Returns by customer and by product", Tab(ReportsHubLinks.CreditNotes), hub),
                new("Top customers", "Biggest buyers, their margin and balance", Tab(ReportsHubLinks.Customers), hub),
            ]),
            new("Stock & buying",
            [
                new("Stock on hand", "Quantity and value by warehouse", Tab(ReportsHubLinks.Stock), hub),
                new("Low stock alerts", "Lines under their minimum or reorder level", Tab(ReportsHubLinks.LowStock), hub),
                new("Slow-moving products", "Stock that has stopped selling", Tab(ReportsHubLinks.SlowMoving), hub),
                new("Top products", "Best sellers by units, sales and margin", Tab(ReportsHubLinks.Products), hub),
                new("Purchasing", "Purchase orders by supplier", Tab(ReportsHubLinks.Purchasing), hub),
            ]),
            new("Field team",
            [
                new("Daily Truck Report", "Load, sales and returns per truck", "/van-sales/reports/daily", VanReportRoles),
                new("Van Sales Performance", "Sales per van and rep", "/van-sales/reports/performance", VanReportRoles),
                new("Attendance Report", "Clock-ins for reps and merchandisers", "/van-sales/attendance-report", VanReportRoles),
                new("Departure Compliance", "Vans leaving the depot on time", "/van-sales/compliance", VanReportRoles),
                new("Route Sales Report", "Sales per route and rep", "/route-sales-report", RouteSalesRoles),
            ]),
        ];
    }

    /// <summary>The groups and reports <paramref name="user"/> may open, narrowed to those matching <paramref name="search"/>.</summary>
    public static IReadOnlyList<ReportsDirectoryGroup> For(ClaimsPrincipal user, ReportsPeriod period, string? search = null)
    {
        var words = (search ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return All(period)
            .Select(group => group with
            {
                Entries = group.Entries
                    .Where(entry => UserRoles.IsInAnyRole(user, entry.Roles))
                    .Where(entry => words.All(word =>
                        $"{entry.Name} {entry.Description} {group.Name}".Contains(word, StringComparison.OrdinalIgnoreCase)))
                    .ToList()
            })
            .Where(group => group.Entries.Count > 0)
            .ToList();
    }
}
