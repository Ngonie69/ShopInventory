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
    /// Depot Controller with access to incoming payments and inventory transfers
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
    /// Merchandiser with access to mobile sales orders and assigned customers
    /// </summary>
    public const string Merchandiser = "Merchandiser";

    /// <summary>
    /// Sales Rep with access to mobile draft sales orders
    /// </summary>
    public const string SalesRep = "SalesRep";

    /// <summary>
    /// Comma-separated role strings for use in [Authorize(Roles = "...")] attributes
    /// </summary>
    public const string InvoicingRoles = "Admin,Cashier";
    public const string PaymentRoles = "Admin,Cashier,DepotController";
    public const string InventoryTransferRoles = "Admin,StockController,DepotController";
    public const string SalesOrderRoles = "Admin,Cashier,Merchandiser,SalesRep";
    public const string PurchasingRoles = "Admin,Manager";

    public const string PodRoles = "Admin,Cashier,PodOperator,SalesRep";

    /// <summary>
    /// Get all available roles
    /// </summary>
    public static IReadOnlyList<string> AllRoles => new[] { Admin, Cashier, StockController, DepotController, Manager, PodOperator, Merchandiser, SalesRep };

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
        string.Equals(role, Cashier, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, DepotController, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Check if a role can create payments
    /// </summary>
    public static bool CanCreatePayments(string role) =>
        IsAdmin(role) ||
        string.Equals(role, Cashier, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, DepotController, StringComparison.OrdinalIgnoreCase);

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
        string.Equals(role, StockController, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, DepotController, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Check if a role requires an assigned warehouse
    /// </summary>
    public static bool RequiresWarehouseAssignment(string role) =>
        string.Equals(role, StockController, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, DepotController, StringComparison.OrdinalIgnoreCase);
}
