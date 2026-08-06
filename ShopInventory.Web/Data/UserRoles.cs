using System.Security.Claims;

namespace ShopInventory.Web.Data;

/// <summary>
/// User roles in the application
/// </summary>
public static class UserRoles
{
    /// <summary>
    /// Administrator with full access to all features. Only role that can create and manage users.
    /// </summary>
    public const string Admin = "Admin";

    /// <summary>
    /// Cashier with access to incoming payments, invoicing, and sales orders
    /// </summary>
    public const string Cashier = "Cashier";

    /// <summary>
    /// Stock Controller with access to inventory transfers only
    /// </summary>
    public const string StockController = "StockController";

    /// <summary>
    /// Depot Controller with access to inventory transfers and local stock only
    /// </summary>
    public const string DepotController = "DepotController";

    /// <summary>
    /// Manager with access to purchasing, reports, and operational oversight
    /// </summary>
    public const string Manager = "Manager";

    /// <summary>
    /// POD Operator with access to Proof of Delivery only
    /// </summary>
    public const string PodOperator = "PodOperator";

    /// <summary>
    /// Driver with access to POD and assigned mobile workflows
    /// </summary>
    public const string Driver = "Driver";

    /// <summary>
    /// Merchandiser with access to mobile sales orders and assigned customers
    /// </summary>
    public const string Merchandiser = "Merchandiser";

    /// <summary>
    /// Laboratory role with access to batch-status management
    /// </summary>
    public const string Lab = "Lab";

    /// <summary>
    /// Sales Rep with access to mobile draft sales orders
    /// </summary>
    public const string SalesRep = "SalesRep";

    /// <summary>
    /// Read-only role for reviewing merchandiser purchase-order attachment reporting
    /// </summary>
    public const string MerchandiserPurchaseOrderViewer = "MerchandiserPurchaseOrderViewer";

    /// <summary>
    /// Comma-separated role strings for use in [Authorize(Roles = "...")] attributes
    /// </summary>
    /// <summary>
    /// Who can open /dashboard. One route, three pages: Home renders the
    /// administrator's dashboard, the sales-rep one for a SalesRep and the
    /// depot one for a DepotController. Cashier, StockController and Manager
    /// used to share it and now land on their own working page instead — see
    /// docs/role-dashboards-plan.md.
    /// </summary>
    public const string DashboardRoles = "Admin,SalesRep,DepotController";

    /// <summary>
    /// Who sees the Overview link in the nav: every role with a workspace of
    /// its own, whether it hangs off /dashboard or carries its own route. The
    /// link's href is resolved per role through <c>RoleLandingRoutes.For</c>,
    /// so this list and that chain have to agree.
    /// </summary>
    public const string DashboardNavRoles = "Admin,SalesRep,DepotController,Cashier,StockController,Manager";
    public const string CatalogueRoles = "Admin,Cashier,StockController,Manager";
    public const string InsightsRoles = "Admin,Cashier,StockController,Manager";
    public const string SystemRoles = "Admin,Cashier,StockController,Manager";
    public const string InvoicingRoles = "Admin,Cashier";
    public const string PaymentRoles = "Admin,Cashier";
    public const string InventoryTransferRoles = "Admin,Manager,StockController,DepotController";
    public const string SalesOrderRoles = "Admin,Cashier,Merchandiser,SalesRep";
    public const string PurchasingRoles = "Admin,Manager";

    /// <summary>
    /// Sales order vs invoice. The insights roles read it across every customer; a sales rep reads
    /// the same report one business partner at a time — see <c>CanReadReportsAcrossCustomers</c>.
    /// </summary>
    public const string OrderFulfillmentReportRoles = "Admin,Cashier,StockController,Manager,SalesRep";

    public const string PodRoles = "Admin,Cashier,PodOperator,Driver,SalesRep";
    public const string UserManagementRoles = "Admin,PodOperator,SalesRep";
    public const string MerchandiserAccountManagementRoles = "Admin,SalesRep";

    /// <summary>
    /// Get all available roles
    /// </summary>
    public static IReadOnlyList<string> AllRoles => new[] { Admin, Cashier, StockController, DepotController, Manager, PodOperator, Driver, Merchandiser, SalesRep, MerchandiserPurchaseOrderViewer, Lab };

    /// <summary>
    /// Whether <paramref name="user"/> may read a report across every customer.
    /// </summary>
    /// <remarks>
    /// A sales rep may not. They reach the sales order vs invoice report through the same page as
    /// everyone else, but always for one business partner they have named — so the statement SAP
    /// runs for them carries that partner, and no other customer's orders are fetched at all.
    /// </remarks>
    public static bool CanReadReportsAcrossCustomers(ClaimsPrincipal user) =>
        InsightsRoles.Split(',').Any(user.IsInRole);

    /// <summary>
    /// Check if a role has admin privileges
    /// </summary>
    public static bool IsAdmin(string role) =>
        string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Check if a role can view/create invoices and credit notes
    /// </summary>
    public static bool CanCreateInvoices(string role) =>
        IsAdmin(role) ||
        string.Equals(role, Cashier, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Check if a role can view payments
    /// </summary>
    public static bool CanViewPayments(string role) =>
        IsAdmin(role) ||
        string.Equals(role, Cashier, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Check if a role can create payments
    /// </summary>
    public static bool CanCreatePayments(string role) =>
        IsAdmin(role) ||
        string.Equals(role, Cashier, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Check if a role can view/create sales orders
    /// </summary>
    public static bool CanViewSalesOrders(string role) =>
        IsAdmin(role) ||
        string.Equals(role, Cashier, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, Merchandiser, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, SalesRep, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Check if a role can view settings
    /// </summary>
    public static bool CanViewSettings(string role) =>
        IsAdmin(role);

    /// <summary>
    /// Check if a role can modify settings
    /// </summary>
    public static bool CanModifySettings(string role) =>
        IsAdmin(role);

    /// <summary>
    /// Check if a role can view audit trail
    /// </summary>
    public static bool CanViewAuditTrail(string role) =>
        IsAdmin(role);

    /// <summary>
    /// Check if a role can manage users (create, edit, delete)
    /// </summary>
    public static bool CanManageUsers(string role) =>
        IsAdmin(role);

    /// <summary>
    /// Check if a role can view products
    /// </summary>
    public static bool CanViewProducts(string role) => true;

    /// <summary>
    /// Check if a role can view prices
    /// </summary>
    public static bool CanViewPrices(string role) => true;

    /// <summary>
    /// Check if a role can view inventory transfers
    /// </summary>
    public static bool CanViewInventoryTransfers(string role) =>
        IsAdmin(role) ||
        string.Equals(role, Manager, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, StockController, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, DepotController, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Check if a role requires an assigned warehouse
    /// </summary>
    public static bool RequiresWarehouseAssignment(string role) =>
        string.Equals(role, StockController, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, DepotController, StringComparison.OrdinalIgnoreCase);
}
