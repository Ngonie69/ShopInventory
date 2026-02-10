namespace ShopInventory.Models;

/// <summary>
/// Defines all available permissions in the system
/// </summary>
public static class Permissions
{
    // Dashboard
    public const string ViewDashboard = "dashboard.view";

    // Products
    public const string ViewProducts = "products.view";
    public const string CreateProducts = "products.create";
    public const string EditProducts = "products.edit";
    public const string DeleteProducts = "products.delete";
    public const string ManageProductPrices = "products.manage_prices";

    // Invoices
    public const string ViewInvoices = "invoices.view";
    public const string CreateInvoices = "invoices.create";
    public const string EditInvoices = "invoices.edit";
    public const string DeleteInvoices = "invoices.delete";
    public const string VoidInvoices = "invoices.void";

    // Payments
    public const string ViewPayments = "payments.view";
    public const string CreatePayments = "payments.create";
    public const string RefundPayments = "payments.refund";
    public const string ProcessRefunds = "payments.process_refunds";

    // Inventory
    public const string ViewStock = "stock.view";
    public const string ViewInventory = "inventory.view";
    public const string EditStock = "stock.edit";
    public const string TransferStock = "stock.transfer";
    public const string TransferInventory = "inventory.transfer";
    public const string AdjustStock = "stock.adjust";
    public const string AdjustInventory = "inventory.adjust";

    // Reports
    public const string ViewReports = "reports.view";
    public const string ExportReports = "reports.export";

    // Customers/Business Partners
    public const string ViewCustomers = "customers.view";
    public const string CreateCustomers = "customers.create";
    public const string EditCustomers = "customers.edit";
    public const string DeleteCustomers = "customers.delete";

    // Users & Admin
    public const string ViewUsers = "users.view";
    public const string CreateUsers = "users.create";
    public const string EditUsers = "users.edit";
    public const string DeleteUsers = "users.delete";
    public const string ManageRoles = "users.manage_roles";
    public const string ManageUserRoles = "users.manage_roles";
    public const string ManagePermissions = "users.manage_permissions";
    public const string ManageUserPermissions = "users.manage_permissions";

    // Settings
    public const string ViewSettings = "settings.view";
    public const string EditSettings = "settings.edit";
    public const string ManageSettings = "settings.manage";
    public const string ManageIntegrations = "settings.integrations";

    // Audit
    public const string ViewAuditLogs = "audit.view";
    public const string ExportAuditLogs = "audit.export";

    // Webhooks
    public const string ViewWebhooks = "webhooks.view";
    public const string ManageWebhooks = "webhooks.manage";

    // Sync & System
    public const string ViewSyncStatus = "sync.view";
    public const string ManageSync = "sync.manage";
    public const string SystemAdmin = "system.admin";
    public const string ManageBackups = "system.backups";

    // Backups
    public const string ViewBackups = "backups.view";
    public const string CreateBackups = "backups.create";
    public const string RestoreBackups = "backups.restore";
    public const string DeleteBackups = "backups.delete";

    /// <summary>
    /// Get all permissions grouped by category
    /// </summary>
    public static Dictionary<string, List<PermissionInfo>> GetAllPermissionsGrouped()
    {
        return new Dictionary<string, List<PermissionInfo>>
        {
            ["Dashboard"] = new()
            {
                new(ViewDashboard, "View Dashboard", "Access the main dashboard")
            },
            ["Products"] = new()
            {
                new(ViewProducts, "View Products", "View product listings and details"),
                new(CreateProducts, "Create Products", "Add new products"),
                new(EditProducts, "Edit Products", "Modify existing products"),
                new(DeleteProducts, "Delete Products", "Remove products from the system"),
                new(ManageProductPrices, "Manage Prices", "Manage product pricing")
            },
            ["Invoices"] = new()
            {
                new(ViewInvoices, "View Invoices", "View invoice listings and details"),
                new(CreateInvoices, "Create Invoices", "Create new invoices"),
                new(EditInvoices, "Edit Invoices", "Modify draft invoices"),
                new(DeleteInvoices, "Delete Invoices", "Delete draft invoices"),
                new(VoidInvoices, "Void Invoices", "Void posted invoices")
            },
            ["Payments"] = new()
            {
                new(ViewPayments, "View Payments", "View payment records"),
                new(CreatePayments, "Create Payments", "Record new payments"),
                new(RefundPayments, "Refund Payments", "Process payment refunds"),
                new(ProcessRefunds, "Process Refunds", "Handle payment refund processing")
            },
            ["Inventory"] = new()
            {
                new(ViewStock, "View Stock", "View stock levels"),
                new(ViewInventory, "View Inventory", "View inventory details"),
                new(TransferStock, "Transfer Stock", "Transfer stock between warehouses"),
                new(TransferInventory, "Transfer Inventory", "Transfer inventory between locations"),
                new(AdjustStock, "Adjust Stock", "Make stock adjustments"),
                new(AdjustInventory, "Adjust Inventory", "Make inventory corrections")
            },
            ["Reports"] = new()
            {
                new(ViewReports, "View Reports", "Access reports and analytics"),
                new(ExportReports, "Export Reports", "Export reports to files")
            },
            ["Customers"] = new()
            {
                new(ViewCustomers, "View Customers", "View customer/business partner information"),
                new(CreateCustomers, "Create Customers", "Add new customers"),
                new(EditCustomers, "Edit Customers", "Modify customer information"),
                new(DeleteCustomers, "Delete Customers", "Remove customers")
            },
            ["Users"] = new()
            {
                new(ViewUsers, "View Users", "View user accounts"),
                new(CreateUsers, "Create Users", "Create new user accounts"),
                new(EditUsers, "Edit Users", "Modify user accounts"),
                new(DeleteUsers, "Delete Users", "Delete user accounts"),
                new(ManageRoles, "Manage Roles", "Assign roles to users"),
                new(ManagePermissions, "Manage Permissions", "Assign granular permissions")
            },
            ["Settings"] = new()
            {
                new(ViewSettings, "View Settings", "View system settings"),
                new(EditSettings, "Edit Settings", "Modify system settings"),
                new(ManageSettings, "Manage Settings", "Full settings management"),
                new(ManageIntegrations, "Manage Integrations", "Configure third-party integrations")
            },
            ["Audit"] = new()
            {
                new(ViewAuditLogs, "View Audit Logs", "Access audit trail"),
                new(ExportAuditLogs, "Export Audit Logs", "Export audit data")
            },
            ["Webhooks"] = new()
            {
                new(ViewWebhooks, "View Webhooks", "View webhook configurations"),
                new(ManageWebhooks, "Manage Webhooks", "Create/edit/delete webhooks")
            },
            ["System"] = new()
            {
                new(ViewSyncStatus, "View Sync Status", "View SAP sync status"),
                new(ManageSync, "Manage Sync", "Trigger sync operations"),
                new(SystemAdmin, "System Admin", "Full system administration access"),
                new(ManageBackups, "Manage Backups", "Manage system backups")
            }
        };
    }

    /// <summary>
    /// Get all permission codes as a flat list
    /// </summary>
    public static List<string> GetAllPermissions()
    {
        return GetAllPermissionsGrouped().Values.SelectMany(p => p.Select(x => x.Code)).ToList();
    }

    /// <summary>
    /// Get default permissions for a role
    /// </summary>
    public static List<string> GetDefaultPermissionsForRole(string role)
    {
        return role switch
        {
            "Admin" => GetAllPermissions(), // Admin gets everything
            "Manager" => new List<string>
            {
                ViewDashboard, ViewProducts, CreateProducts, EditProducts, ManageProductPrices,
                ViewInvoices, CreateInvoices, EditInvoices, VoidInvoices,
                ViewPayments, CreatePayments, RefundPayments, ProcessRefunds,
                ViewStock, ViewInventory, TransferStock, TransferInventory, AdjustStock, AdjustInventory,
                ViewReports, ExportReports,
                ViewCustomers, CreateCustomers, EditCustomers,
                ViewUsers,
                ViewSettings, EditSettings,
                ViewAuditLogs,
                ViewSyncStatus
            },
            "User" => new List<string>
            {
                ViewDashboard, ViewProducts,
                ViewInvoices, CreateInvoices,
                ViewPayments, CreatePayments,
                ViewStock, ViewInventory,
                ViewCustomers
            },
            "ReadOnly" => new List<string>
            {
                ViewDashboard, ViewProducts, ViewInvoices, ViewPayments,
                ViewStock, ViewInventory, ViewCustomers, ViewReports
            },
            _ => new List<string> { ViewDashboard }
        };
    }
}

/// <summary>
/// Alias for Permissions class (singular form for convenience)
/// </summary>
public static class Permission
{
    // Dashboard
    public const string ViewDashboard = Permissions.ViewDashboard;

    // Products
    public const string ViewProducts = Permissions.ViewProducts;
    public const string CreateProducts = Permissions.CreateProducts;
    public const string EditProducts = Permissions.EditProducts;
    public const string DeleteProducts = Permissions.DeleteProducts;
    public const string ManageProductPrices = Permissions.ManageProductPrices;

    // Invoices
    public const string ViewInvoices = Permissions.ViewInvoices;
    public const string CreateInvoices = Permissions.CreateInvoices;
    public const string EditInvoices = Permissions.EditInvoices;
    public const string DeleteInvoices = Permissions.DeleteInvoices;
    public const string VoidInvoices = Permissions.VoidInvoices;

    // Payments
    public const string ViewPayments = Permissions.ViewPayments;
    public const string CreatePayments = Permissions.CreatePayments;
    public const string RefundPayments = Permissions.RefundPayments;
    public const string ProcessRefunds = Permissions.ProcessRefunds;

    // Inventory
    public const string ViewStock = Permissions.ViewStock;
    public const string ViewInventory = Permissions.ViewInventory;
    public const string EditStock = Permissions.EditStock;
    public const string TransferStock = Permissions.TransferStock;
    public const string TransferInventory = Permissions.TransferInventory;
    public const string AdjustStock = Permissions.AdjustStock;
    public const string AdjustInventory = Permissions.AdjustInventory;

    // Reports
    public const string ViewReports = Permissions.ViewReports;
    public const string ExportReports = Permissions.ExportReports;

    // Customers/Business Partners
    public const string ViewCustomers = Permissions.ViewCustomers;
    public const string CreateCustomers = Permissions.CreateCustomers;
    public const string EditCustomers = Permissions.EditCustomers;
    public const string DeleteCustomers = Permissions.DeleteCustomers;

    // Users & Admin
    public const string ViewUsers = Permissions.ViewUsers;
    public const string CreateUsers = Permissions.CreateUsers;
    public const string EditUsers = Permissions.EditUsers;
    public const string DeleteUsers = Permissions.DeleteUsers;
    public const string ManageRoles = Permissions.ManageRoles;
    public const string ManageUserRoles = Permissions.ManageUserRoles;
    public const string ManagePermissions = Permissions.ManagePermissions;
    public const string ManageUserPermissions = Permissions.ManageUserPermissions;

    // Settings
    public const string ViewSettings = Permissions.ViewSettings;
    public const string EditSettings = Permissions.EditSettings;
    public const string ManageSettings = Permissions.ManageSettings;
    public const string ManageIntegrations = Permissions.ManageIntegrations;

    // Audit
    public const string ViewAuditLogs = Permissions.ViewAuditLogs;
    public const string ExportAuditLogs = Permissions.ExportAuditLogs;

    // Webhooks
    public const string ViewWebhooks = Permissions.ViewWebhooks;
    public const string ManageWebhooks = Permissions.ManageWebhooks;

    // Sync & System
    public const string ViewSyncStatus = Permissions.ViewSyncStatus;
    public const string ManageSync = Permissions.ManageSync;
    public const string SystemAdmin = Permissions.SystemAdmin;
    public const string ManageBackups = Permissions.ManageBackups;

    // Backups
    public const string ViewBackups = Permissions.ViewBackups;
    public const string CreateBackups = Permissions.CreateBackups;
    public const string RestoreBackups = Permissions.RestoreBackups;
    public const string DeleteBackups = Permissions.DeleteBackups;

    /// <summary>
    /// Get all permissions grouped by category (delegates to Permissions)
    /// </summary>
    public static Dictionary<string, List<PermissionInfo>> GetAllPermissionsGrouped()
        => Permissions.GetAllPermissionsGrouped();

    /// <summary>
    /// Get all permission codes as a flat list (delegates to Permissions)
    /// </summary>
    public static List<string> GetAllPermissions()
        => Permissions.GetAllPermissions();

    /// <summary>
    /// Get default permissions for a role (delegates to Permissions)
    /// </summary>
    public static List<string> GetDefaultPermissionsForRole(string role)
        => Permissions.GetDefaultPermissionsForRole(role);
}

/// <summary>
/// Permission information record
/// </summary>
public record PermissionInfo(string Code, string Name, string Description);
