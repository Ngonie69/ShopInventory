namespace ShopInventory.Features.Notifications;

public static class NotificationAudienceRules
{
    public static readonly string[] GlobalBroadcastCategories = ["System", "Security", "SAP"];
    public static readonly string[] SystemBroadcastCategories = ["System", "SAP"];
    public static readonly string[] SecurityBroadcastCategories = ["Security"];
    public static readonly string[] SalesBroadcastCategories = ["SalesOrder", "Quotation", "Customer"];
    public static readonly string[] InvoiceBroadcastCategories = ["Invoice", "CreditNote"];
    public static readonly string[] PaymentBroadcastCategories = ["Payment", "IncomingPayment"];
    public static readonly string[] InventoryBroadcastCategories = ["LowStock", "Stock", "Inventory", "InventoryTransfer", "TransferRequest", "TransferApproval"];
    public static readonly string[] PurchasingBroadcastCategories = ["PurchaseRequest", "PurchaseQuotation", "PurchaseOrder", "PurchaseInvoice", "GoodsReceiptPurchaseOrder"];
    public static readonly string[] PodBroadcastCategories = ["POD", "ProofOfDelivery"];
    public static readonly string[] AppVersionBroadcastCategories = ["AppVersion"];
    public static readonly string[] LabBroadcastCategories = ["Lab", "Batch", "BatchStatus"];

    public static readonly string[] SalesAudienceRoles = ["Admin", "Cashier", "SalesRep", "Merchandiser", "ADR", "Sales"];
    public static readonly string[] InvoiceAudienceRoles = ["Admin", "Cashier", "Sales"];
    public static readonly string[] PaymentAudienceRoles = ["Admin", "Cashier", "DepotController"];
    public static readonly string[] InventoryAudienceRoles = ["Admin", "StockController", "DepotController"];
    public static readonly string[] PurchasingAudienceRoles = ["Admin", "Manager"];
    public static readonly string[] PodAudienceRoles = ["Admin", "Cashier", "PodOperator", "SalesRep"];
    public static readonly string[] AppVersionAudienceRoles = ["Driver", "PodOperator", "Merchandiser"];
    public static readonly string[] SystemAudienceRoles = ["Admin", "Cashier", "StockController", "DepotController", "Manager"];
    public static readonly string[] SecurityAudienceRoles = ["Admin", "Cashier", "StockController", "DepotController", "Manager", "PodOperator", "Merchandiser", "SalesRep", "MerchandiserPurchaseOrderViewer"];
    public static readonly string[] DashboardAudienceRoles = ["Admin", "Cashier", "StockController", "DepotController", "Manager", "SalesRep"];
    public static readonly string[] SalesOrderPageAudienceRoles = ["Admin", "Cashier", "SalesRep"];
    public static readonly string[] SalesOrderEditAudienceRoles = ["Admin", "Cashier", "Merchandiser", "SalesRep"];
    public static readonly string[] QuotationAudienceRoles = ["Admin", "Cashier"];
    public static readonly string[] MobileSalesAudienceRoles = ["Admin", "Cashier", "Merchandiser", "SalesRep", "ADR", "Sales"];
    public static readonly string[] MerchandiserAccountAudienceRoles = ["Admin", "SalesRep"];
    public static readonly string[] CatalogueAudienceRoles = ["Admin", "Cashier", "StockController", "DepotController", "Manager", "Merchandiser"];
    public static readonly string[] ReportAudienceRoles = ["Admin", "Cashier", "StockController", "DepotController", "Manager"];
    public static readonly string[] MerchandiserReportAudienceRoles = ["Admin", "Manager", "MerchandiserPurchaseOrderViewer"];
    public static readonly string[] TimesheetAudienceRoles = ["Admin", "Manager", "SalesRep", "Merchandiser"];
    public static readonly string[] DocumentAudienceRoles = ["Admin", "Cashier", "StockController", "DepotController", "Manager", "PodOperator", "Driver", "Merchandiser", "SalesRep", "MerchandiserPurchaseOrderViewer"];
    public static readonly string[] ManagementAudienceRoles = ["Admin", "Manager"];
    public static readonly string[] DesktopSalesAudienceRoles = ["Admin", "Cashier"];
    public static readonly string[] LabAudienceRoles = ["Admin", "Lab"];
    public static readonly string[] AdminAudienceRoles = ["Admin"];

    public static string[] NormalizeRoles(IEnumerable<string>? roles) =>
        (roles ?? [])
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool IsAdmin(IEnumerable<string>? roles) => HasAnyRole(roles, AdminAudienceRoles);

    public static bool HasAnyRole(IEnumerable<string>? roles, IEnumerable<string> allowedRoles)
    {
        var normalizedRoles = NormalizeRoles(roles);
        return allowedRoles.Any(allowedRole =>
            normalizedRoles.Any(role => string.Equals(role, allowedRole, StringComparison.OrdinalIgnoreCase)));
    }

    public static string[] GetBroadcastAudienceRoles(string? category, string? actionUrl = null)
    {
        var actionAudience = GetActionUrlAudienceRoles(actionUrl);
        if (actionAudience.Length > 0)
        {
            return actionAudience;
        }

        if (string.IsNullOrWhiteSpace(category))
        {
            return AdminAudienceRoles;
        }

        if (SecurityBroadcastCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
        {
            return SecurityAudienceRoles;
        }

        if (SystemBroadcastCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
        {
            return SystemAudienceRoles;
        }

        if (SalesBroadcastCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
        {
            return SalesAudienceRoles;
        }

        if (InvoiceBroadcastCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
        {
            return InvoiceAudienceRoles;
        }

        if (PaymentBroadcastCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
        {
            return PaymentAudienceRoles;
        }

        if (InventoryBroadcastCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
        {
            return InventoryAudienceRoles;
        }

        if (PurchasingBroadcastCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
        {
            return PurchasingAudienceRoles;
        }

        if (PodBroadcastCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
        {
            return PodAudienceRoles;
        }

        if (AppVersionBroadcastCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
        {
            return AppVersionAudienceRoles;
        }

        if (LabBroadcastCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
        {
            return LabAudienceRoles;
        }

        return AdminAudienceRoles;
    }

    public static bool CategoryRequiresActionUrl(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return false;
        }

        return SalesBroadcastCategories.Contains(category, StringComparer.OrdinalIgnoreCase) ||
               InvoiceBroadcastCategories.Contains(category, StringComparer.OrdinalIgnoreCase) ||
               PaymentBroadcastCategories.Contains(category, StringComparer.OrdinalIgnoreCase) ||
               InventoryBroadcastCategories.Contains(category, StringComparer.OrdinalIgnoreCase) ||
               PurchasingBroadcastCategories.Contains(category, StringComparer.OrdinalIgnoreCase) ||
               PodBroadcastCategories.Contains(category, StringComparer.OrdinalIgnoreCase) ||
               AppVersionBroadcastCategories.Contains(category, StringComparer.OrdinalIgnoreCase) ||
               LabBroadcastCategories.Contains(category, StringComparer.OrdinalIgnoreCase);
    }

    public static string[] GetActionUrlAudienceRoles(string? actionUrl)
    {
        var normalizedActionUrl = NormalizeActionUrl(actionUrl);
        if (string.IsNullOrWhiteSpace(normalizedActionUrl))
        {
            return [];
        }

        if (string.Equals(normalizedActionUrl, "/", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/dashboard", StringComparison.OrdinalIgnoreCase))
        {
            return DashboardAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/lab/batch-status", StringComparison.OrdinalIgnoreCase))
        {
            return LabAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/sales-orders/edit", StringComparison.OrdinalIgnoreCase))
        {
            return SalesOrderEditAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/security", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/notifications", StringComparison.OrdinalIgnoreCase))
        {
            return SecurityAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/mobile-drafts", StringComparison.OrdinalIgnoreCase))
        {
            return MobileSalesAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/timesheets", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/timesheet-report", StringComparison.OrdinalIgnoreCase))
        {
            return TimesheetAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/merchandiser-account", StringComparison.OrdinalIgnoreCase))
        {
            return MerchandiserAccountAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/sales-orders", StringComparison.OrdinalIgnoreCase))
        {
            return SalesOrderPageAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/quotations", StringComparison.OrdinalIgnoreCase))
        {
            return QuotationAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/invoices", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/credit-notes", StringComparison.OrdinalIgnoreCase))
        {
            return InvoiceAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/payments", StringComparison.OrdinalIgnoreCase))
        {
            return PaymentAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/inventory-transfers", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/inventory-transfer", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/transfer-request", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/local-stock", StringComparison.OrdinalIgnoreCase))
        {
            return InventoryAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/products", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/stock", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/customers", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/gl-accounts", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/prices", StringComparison.OrdinalIgnoreCase))
        {
            return CatalogueAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/purchase-orders", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/purchase-requests", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/purchase-quotations", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/goods-receipt-pos", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/purchase-invoices", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/fiscalized-sales-report", StringComparison.OrdinalIgnoreCase))
        {
            return PurchasingAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/pod-dashboard", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/pods", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/pod-report", StringComparison.OrdinalIgnoreCase))
        {
            return PodAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/reports/merchandiser-purchase-orders", StringComparison.OrdinalIgnoreCase))
        {
            return MerchandiserReportAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/reports", StringComparison.OrdinalIgnoreCase))
        {
            return ReportAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/document-templates", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/documents", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/document-manager", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/sync-status", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/exception-center", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/ai-assistant", StringComparison.OrdinalIgnoreCase))
        {
            return SystemAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/user-activity", StringComparison.OrdinalIgnoreCase))
        {
            return ManagementAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/desktop-sales", StringComparison.OrdinalIgnoreCase))
        {
            return DesktopSalesAudienceRoles;
        }

        if (normalizedActionUrl.StartsWith("/settings", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/revmax", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/desktop-transactions", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/user-management", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/driver-business-partners", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/merchandiser-products", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/customer-portal-management", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/audit-trail", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/backups", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/webhooks", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) ||
            normalizedActionUrl.StartsWith("/api-explorer", StringComparison.OrdinalIgnoreCase))
        {
            return AdminAudienceRoles;
        }

        return [];
    }

    public static string? NormalizeActionUrl(string? actionUrl)
    {
        if (string.IsNullOrWhiteSpace(actionUrl))
        {
            return null;
        }

        var trimmedActionUrl = actionUrl.Trim();
        var queryIndex = trimmedActionUrl.IndexOf('?');
        if (queryIndex >= 0)
        {
            trimmedActionUrl = trimmedActionUrl[..queryIndex];
        }

        var fragmentIndex = trimmedActionUrl.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            trimmedActionUrl = trimmedActionUrl[..fragmentIndex];
        }

        if (!trimmedActionUrl.StartsWith('/'))
        {
            trimmedActionUrl = "/" + trimmedActionUrl;
        }

        return trimmedActionUrl;
    }
}
