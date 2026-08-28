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
    // Every category a producer writes must appear in one of these lists. The non-admin branch of
    // BuildVisibleNotificationsQuery admits a row only if its category matches a list the viewer's
    // roles can see, so an unlisted category is stored and then shown to nobody — which is what
    // happened to "ProductCatalog" for its whole life. That category is gone rather than fixed: a
    // catalogue change is a signal the merchandiser app acts on, not news for the merchandiser, and
    // it now goes out as a data-only push (see ProductCatalogChangedNotificationHandler). Rows
    // written before that are no longer shown to anyone and expire on their own within 30 days.
    public static readonly string[] AppVersionBroadcastCategories = ["AppVersion"];
    public static readonly string[] LabBroadcastCategories = ["Lab", "Batch", "BatchStatus"];

    public static readonly string[] SalesAudienceRoles = ["Admin", "Cashier", "SalesRep", "Merchandiser", "ADR", "Sales"];
    public static readonly string[] InvoiceAudienceRoles = ["Admin", "Cashier", "Sales"];
    public static readonly string[] PaymentAudienceRoles = ["Admin", "Cashier"];
    public static readonly string[] InventoryAudienceRoles = ["Admin", "StockController", "DepotController"];
    public static readonly string[] PurchasingAudienceRoles = ["Admin", "Manager"];
    public static readonly string[] PodAudienceRoles = ["Admin", "Cashier", "PodOperator", "SalesRep"];
    public static readonly string[] AppVersionAudienceRoles = ["Driver", "PodOperator", "Merchandiser"];
    public static readonly string[] SystemAudienceRoles = ["Admin", "Cashier", "StockController", "Manager"];
    public static readonly string[] SecurityAudienceRoles = ["Admin", "Cashier", "StockController", "DepotController", "Manager", "PodOperator", "Merchandiser", "SalesRep", "MerchandiserPurchaseOrderViewer"];
    public static readonly string[] DashboardAudienceRoles = ["Admin", "Cashier", "StockController", "Manager", "SalesRep"];
    public static readonly string[] SalesOrderPageAudienceRoles = ["Admin", "Cashier", "SalesRep"];
    public static readonly string[] SalesOrderEditAudienceRoles = ["Admin", "Cashier", "Merchandiser", "SalesRep"];
    // Matches who can open /quotations. A rep who raises a quotation is told about it; leave them
    // out and their own document's notification is stored and shown to nobody.
    public static readonly string[] QuotationAudienceRoles = ["Admin", "Cashier", "SalesRep"];
    public static readonly string[] MobileSalesAudienceRoles = ["Admin", "Cashier", "Merchandiser", "SalesRep", "ADR", "Sales"];
    public static readonly string[] MerchandiserAccountAudienceRoles = ["Admin", "SalesRep"];
    public static readonly string[] CatalogueAudienceRoles = ["Admin", "Cashier", "StockController", "Manager", "Merchandiser"];
    public static readonly string[] ReportAudienceRoles = ["Admin", "Cashier", "StockController", "Manager"];
    public static readonly string[] MerchandiserReportAudienceRoles = ["Admin", "Manager", "MerchandiserPurchaseOrderViewer"];
    public static readonly string[] TimesheetAudienceRoles = ["Admin", "Manager", "SalesRep", "Merchandiser"];
    public static readonly string[] DocumentAudienceRoles = ["Admin", "Cashier", "StockController", "Manager", "PodOperator", "Driver", "Merchandiser", "SalesRep", "MerchandiserPurchaseOrderViewer"];
    public static readonly string[] ManagementAudienceRoles = ["Admin", "Manager"];
    public static readonly string[] DesktopSalesAudienceRoles = ["Admin", "Cashier"];
    public static readonly string[] LabAudienceRoles = ["Admin", "Lab"];
    public static readonly string[] AdminAudienceRoles = ["Admin"];

    /// <summary>
    /// Roles that receive only notifications addressed to them — by username, or to their own role
    /// — and never a broadcast sent to whoever happens to be in an audience.
    /// </summary>
    /// <remarks>
    /// Merchandiser is a mobile-only role. What a merchandiser is answerable for is the orders they
    /// submitted, and that is what their app shows them; a company-wide broadcast on that phone is
    /// noise at best, and at worst an alert about a page the app does not have. Membership of
    /// <see cref="SalesAudienceRoles"/> and the rest still stands, because a notification addressed
    /// to a merchandiser personally — their own order posting, failing, being held — has to pass the
    /// same category filter as any other. This narrows who a broadcast goes out to, not what an
    /// addressed notification may say.
    /// </remarks>
    public static readonly string[] AddressedOnlyRoles = ["Merchandiser"];

    /// <summary>
    /// Whether these roles are sent notifications addressed to nobody in particular. Both halves of
    /// delivery have to ask: <see cref="GetBroadcastAudienceRoles"/> for the push and the SignalR
    /// groups, and the visibility query for the list.
    /// </summary>
    public static bool ReceivesBroadcasts(IEnumerable<string>? roles) => !HasAnyRole(roles, AddressedOnlyRoles);

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

    /// <summary>
    /// Who a broadcast — a notification with neither a target user nor a target role — is delivered
    /// to, over SignalR and mobile push.
    /// </summary>
    /// <remarks>
    /// This has to agree with <c>NotificationService.BuildVisibleNotificationsQuery</c>, which
    /// admits a broadcast to a non-admin only when both of its filters pass: the category is one the
    /// viewer's roles can see, <em>and</em> the action URL is a route they can open. Returning the
    /// action-URL audience on its own — which is what this did — delivered rows to roles the list
    /// then hid from them.
    ///
    /// The morning low-stock sweep is the case that reached production: category "LowStock" with
    /// ActionUrl "/stock" resolved to the Catalogue audience, which includes Merchandiser, so every
    /// merchandiser's phone rang at 07:30 with a stock-control alert that never appeared in their
    /// notification list. The route decides which roles may follow the link; it cannot widen the
    /// audience past the ones the category is for.
    ///
    /// Admins are always included, because the query shows them every untargeted broadcast — and
    /// because that keeps this non-empty, which is what stops <c>CreateNotificationAsync</c> falling
    /// through to its send-to-everyone branch. <see cref="AddressedOnlyRoles"/> are always dropped:
    /// they are sent what is addressed to them and nothing else.
    /// </remarks>
    public static string[] GetBroadcastAudienceRoles(string? category, string? actionUrl = null)
    {
        var categoryAudience = GetCategoryAudienceRoles(category);

        // No action URL leaves the category audience alone: the query's route filter admits a row
        // with no ActionUrl to everyone. A URL that matches no route rule narrows the audience to
        // nothing, because that same filter admits an unrecognised route to no non-admin either.
        var audience = string.IsNullOrWhiteSpace(actionUrl)
            ? categoryAudience
            : Intersect(categoryAudience, GetActionUrlAudienceRoles(actionUrl));

        return AdminAudienceRoles
            .Concat(audience.Where(role => ReceivesBroadcasts([role])))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] Intersect(string[] left, string[] right) =>
        left.Where(role => right.Contains(role, StringComparer.OrdinalIgnoreCase)).ToArray();

    /// <summary>
    /// The roles a category is written for, matching the category filter in
    /// <c>BuildVisibleNotificationsQuery</c>. A category in no list belongs to no one but Admin,
    /// which is exactly how the query treats it.
    /// </summary>
    public static string[] GetCategoryAudienceRoles(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return [];
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

        return [];
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
        // Routed on the path alone: which page a notification opens decides who may see it, and
        // which record it opens there does not.
        var normalizedActionUrl = GetActionUrlPath(actionUrl);
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
            normalizedActionUrl.StartsWith("/purchase-invoices", StringComparison.OrdinalIgnoreCase))
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

        // "/revmax" is kept for notifications raised before that page was removed: their action URLs
        // are persisted, so dropping the prefix would silently re-route the audience for old rows.
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

    /// <summary>
    /// The link as it is stored on the notification and followed when it is clicked, so it keeps
    /// its query string.
    /// </summary>
    /// <remarks>
    /// This used to truncate at the '?', which is where the audience is decided but not where the
    /// link is built. A transfer approval pointing at
    /// "/inventory-transfers?requestDocEntry=23055" was stored as bare "/inventory-transfers", and
    /// the page reads that parameter to open the document — so the authorizer landed on an
    /// unfiltered list of every transfer request and had to find theirs by hand. Use
    /// <see cref="GetActionUrlPath"/> wherever the route, rather than the record, is the question.
    /// </remarks>
    public static string? NormalizeActionUrl(string? actionUrl)
    {
        if (string.IsNullOrWhiteSpace(actionUrl))
        {
            return null;
        }

        var trimmedActionUrl = actionUrl.Trim();
        if (!trimmedActionUrl.StartsWith('/'))
        {
            trimmedActionUrl = "/" + trimmedActionUrl;
        }

        return trimmedActionUrl;
    }

    /// <summary>
    /// The route part of an action URL, with any query string and fragment dropped — what the
    /// audience rules match on.
    /// </summary>
    public static string? GetActionUrlPath(string? actionUrl)
    {
        var normalizedActionUrl = NormalizeActionUrl(actionUrl);
        if (normalizedActionUrl is null)
        {
            return null;
        }

        var queryIndex = normalizedActionUrl.IndexOf('?');
        if (queryIndex >= 0)
        {
            normalizedActionUrl = normalizedActionUrl[..queryIndex];
        }

        var fragmentIndex = normalizedActionUrl.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            normalizedActionUrl = normalizedActionUrl[..fragmentIndex];
        }

        return normalizedActionUrl;
    }
}
