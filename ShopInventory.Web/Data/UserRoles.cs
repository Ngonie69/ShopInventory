namespace ShopInventory.Web.Data;

/// <summary>
/// User roles in the application
/// </summary>
public static class UserRoles
{
    /// <summary>
    /// Administrator with full access to all features
    /// </summary>
    public const string Admin = "Admin";

    /// <summary>
    /// Sales person with access to invoices, payments, and product views
    /// </summary>
    public const string SalesPerson = "SalesPerson";

    /// <summary>
    /// ADR role with limited access
    /// </summary>
    public const string ADR = "ADR";

    /// <summary>
    /// Get all available roles
    /// </summary>
    public static IReadOnlyList<string> AllRoles => new[] { Admin, SalesPerson, ADR };

    /// <summary>
    /// Check if a role has admin privileges
    /// </summary>
    public static bool IsAdmin(string role) =>
        string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Check if a role can create invoices
    /// </summary>
    public static bool CanCreateInvoices(string role) =>
        IsAdmin(role) ||
        string.Equals(role, SalesPerson, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Check if a role can view payments
    /// </summary>
    public static bool CanViewPayments(string role) =>
        IsAdmin(role) ||
        string.Equals(role, SalesPerson, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Check if a role can create payments
    /// </summary>
    public static bool CanCreatePayments(string role) =>
        IsAdmin(role) ||
        string.Equals(role, SalesPerson, StringComparison.OrdinalIgnoreCase);

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
        string.Equals(role, SalesPerson, StringComparison.OrdinalIgnoreCase);
}
