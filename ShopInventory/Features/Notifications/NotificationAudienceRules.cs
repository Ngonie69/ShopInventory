namespace ShopInventory.Features.Notifications;

public static class NotificationAudienceRules
{
    public static readonly string[] GlobalBroadcastCategories = ["System", "Security", "SAP"];
    public static readonly string[] SalesBroadcastCategories = ["SalesOrder", "Quotation"];
    public static readonly string[] InvoiceBroadcastCategories = ["Invoice", "CreditNote"];
    public static readonly string[] PaymentBroadcastCategories = ["Payment", "IncomingPayment"];
    public static readonly string[] InventoryBroadcastCategories = ["LowStock", "Stock", "Inventory", "InventoryTransfer", "TransferRequest"];
    public static readonly string[] PurchasingBroadcastCategories = ["PurchaseRequest", "PurchaseQuotation", "PurchaseOrder", "PurchaseInvoice", "GoodsReceiptPurchaseOrder"];
    public static readonly string[] PodBroadcastCategories = ["POD", "ProofOfDelivery"];

    public static readonly string[] SalesAudienceRoles = ["Admin", "Cashier", "SalesRep", "Merchandiser"];
    public static readonly string[] InvoiceAudienceRoles = ["Admin", "Cashier"];
    public static readonly string[] PaymentAudienceRoles = ["Admin", "Cashier", "DepotController"];
    public static readonly string[] InventoryAudienceRoles = ["Admin", "StockController", "DepotController"];
    public static readonly string[] PurchasingAudienceRoles = ["Admin", "Manager"];
    public static readonly string[] PodAudienceRoles = ["Admin", "Cashier", "PodOperator", "Driver", "SalesRep"];
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

    public static string[] GetBroadcastAudienceRoles(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return AdminAudienceRoles;
        }

        if (GlobalBroadcastCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
        {
            return [];
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

        return AdminAudienceRoles;
    }
}