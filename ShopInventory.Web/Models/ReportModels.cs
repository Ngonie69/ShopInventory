namespace ShopInventory.Web.Models;

#region Report Models

/// <summary>
/// Sales summary report
/// </summary>
public class SalesSummaryReport
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int TotalInvoices { get; set; }
    public decimal TotalSalesUSD { get; set; }
    public decimal TotalSalesZIG { get; set; }
    public decimal TotalVatUSD { get; set; }
    public decimal TotalVatZIG { get; set; }
    public decimal AverageInvoiceValueUSD { get; set; }
    public decimal AverageInvoiceValueZIG { get; set; }
    public int UniqueCustomers { get; set; }
    public List<DailySales> DailySales { get; set; } = new();
    public List<SalesByCurrency> SalesByCurrency { get; set; } = new();
}

public class DailySales
{
    public DateTime Date { get; set; }
    public int InvoiceCount { get; set; }
    public decimal TotalSalesUSD { get; set; }
    public decimal TotalSalesZIG { get; set; }
}

public class SalesByCurrency
{
    public string Currency { get; set; } = string.Empty;
    public int InvoiceCount { get; set; }
    public decimal TotalSales { get; set; }
    public decimal TotalVat { get; set; }
}

/// <summary>
/// Top products report
/// </summary>
public class TopProductsReport
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int TotalProductsSold { get; set; }
    public List<TopProduct> TopProducts { get; set; } = new();
}

public class TopProduct
{
    public int Rank { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public decimal TotalQuantitySold { get; set; }
    public decimal TotalRevenueUSD { get; set; }
    public decimal TotalRevenueZIG { get; set; }
    public int TimesOrdered { get; set; }
}

/// <summary>
/// Stock summary report
/// </summary>
public class StockSummaryReport
{
    public DateTime ReportDate { get; set; }
    public int TotalProducts { get; set; }
    public int ProductsInStock { get; set; }
    public int ProductsOutOfStock { get; set; }
    public int ProductsBelowReorderLevel { get; set; }
    public decimal TotalStockValueUSD { get; set; }
    public decimal TotalStockValueZIG { get; set; }
    public List<StockByWarehouse> StockByWarehouse { get; set; } = new();
}

public class StockByWarehouse
{
    public string WarehouseCode { get; set; } = string.Empty;
    public string WarehouseName { get; set; } = string.Empty;
    public int ProductCount { get; set; }
    public decimal TotalQuantity { get; set; }
    public decimal TotalValueUSD { get; set; }
    public decimal TotalValueZIG { get; set; }
}

/// <summary>
/// Low stock alert report
/// </summary>
public class LowStockAlertReport
{
    public DateTime ReportDate { get; set; }
    public int TotalAlerts { get; set; }
    public int CriticalCount { get; set; }
    public int WarningCount { get; set; }
    public List<LowStockItem> Items { get; set; } = new();
}

public class LowStockItem
{
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string WarehouseCode { get; set; } = string.Empty;
    public decimal CurrentStock { get; set; }
    public decimal ReorderLevel { get; set; }
    public decimal MinimumStock { get; set; }
    public string AlertLevel { get; set; } = string.Empty;
    public decimal SuggestedReorderQty { get; set; }
}

/// <summary>
/// Payment summary report
/// </summary>
public class PaymentSummaryReport
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int TotalPayments { get; set; }
    public decimal TotalAmountUSD { get; set; }
    public decimal TotalAmountZIG { get; set; }
    public List<PaymentByMethod> PaymentsByMethod { get; set; } = new();
    public List<DailyPayment> DailyPayments { get; set; } = new();
}

public class PaymentByMethod
{
    public string PaymentMethod { get; set; } = string.Empty;
    public int PaymentCount { get; set; }
    public decimal TotalAmountUSD { get; set; }
    public decimal TotalAmountZIG { get; set; }
    public decimal PercentageOfTotal { get; set; }
}

public class DailyPayment
{
    public DateTime Date { get; set; }
    public int PaymentCount { get; set; }
    public decimal TotalAmountUSD { get; set; }
    public decimal TotalAmountZIG { get; set; }
}

/// <summary>
/// Top customers report
/// </summary>
public class TopCustomersReport
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int TotalCustomers { get; set; }
    public List<TopCustomer> TopCustomers { get; set; } = new();
}

public class TopCustomer
{
    public int Rank { get; set; }
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public int InvoiceCount { get; set; }
    public decimal TotalPurchasesUSD { get; set; }
    public decimal TotalPurchasesZIG { get; set; }
    public decimal TotalPaymentsUSD { get; set; }
    public decimal TotalPaymentsZIG { get; set; }
    public decimal OutstandingBalanceUSD { get; set; }
    public decimal OutstandingBalanceZIG { get; set; }
}

/// <summary>
/// Order fulfillment report
/// </summary>
public class OrderFulfillmentReport
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int TotalOrders { get; set; }
    public int OpenOrders { get; set; }
    public int ClosedOrders { get; set; }
    public int CancelledOrders { get; set; }
    public decimal FulfillmentRatePercent { get; set; }
    public decimal TotalOrderValueUSD { get; set; }
    public decimal TotalOrderValueZIG { get; set; }
    public decimal TotalDeliveredValueUSD { get; set; }
    public decimal TotalDeliveredValueZIG { get; set; }
    public decimal TotalPendingValueUSD { get; set; }
    public decimal TotalPendingValueZIG { get; set; }
    public decimal AverageOrderValueUSD { get; set; }
    public int TotalLineItems { get; set; }
    public int FullyDeliveredLines { get; set; }
    public int PartiallyDeliveredLines { get; set; }
    public int UndeliveredLines { get; set; }
    public List<OrderFulfillmentItem> Orders { get; set; } = new();
    public List<FulfillmentByCustomer> FulfillmentByCustomer { get; set; } = new();
    public List<DailyFulfillment> DailyFulfillment { get; set; } = new();
}

public class OrderFulfillmentItem
{
    public int DocNum { get; set; }
    public int DocEntry { get; set; }
    public DateTime OrderDate { get; set; }
    public DateTime DueDate { get; set; }
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public string DocCurrency { get; set; } = string.Empty;
    public decimal OrderTotal { get; set; }
    public string Status { get; set; } = string.Empty;
    public int TotalLines { get; set; }
    public int DeliveredLines { get; set; }
    public decimal TotalQuantityOrdered { get; set; }
    public decimal TotalQuantityDelivered { get; set; }
    public decimal TotalQuantityPending { get; set; }
    public decimal FulfillmentPercent { get; set; }
    public bool IsOverdue { get; set; }
    public int DaysOverdue { get; set; }
    public List<OrderLineDetail> Lines { get; set; } = new();
}

public class OrderLineDetail
{
    public string ItemCode { get; set; } = string.Empty;
    public string ItemDescription { get; set; } = string.Empty;
    public string WarehouseCode { get; set; } = string.Empty;
    public decimal QuantityOrdered { get; set; }
    public decimal QuantityDelivered { get; set; }
    public decimal QuantityPending { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public decimal InvoicedValue { get; set; }
    public string LineStatus { get; set; } = string.Empty;
    public string InvoiceNumbers { get; set; } = string.Empty;
}

public class FulfillmentByCustomer
{
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public int TotalOrders { get; set; }
    public int OpenOrders { get; set; }
    public int ClosedOrders { get; set; }
    public decimal TotalOrderValue { get; set; }
    public decimal FulfillmentRatePercent { get; set; }
    public decimal TotalPendingValue { get; set; }
}

public class DailyFulfillment
{
    public DateTime Date { get; set; }
    public int OrdersPlaced { get; set; }
    public int OrdersClosed { get; set; }
    public decimal OrderValueUSD { get; set; }
    public decimal QuantityOrdered { get; set; }
    public decimal QuantityDelivered { get; set; }
}

#endregion

#region Credit Notes Report

public class CreditNoteSummaryReport
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int TotalCreditNotes { get; set; }
    public decimal TotalCreditAmountUSD { get; set; }
    public decimal TotalCreditAmountZIG { get; set; }
    public decimal TotalVatUSD { get; set; }
    public decimal TotalVatZIG { get; set; }
    public decimal AverageCreditNoteValueUSD { get; set; }
    public int UniqueCustomers { get; set; }
    public decimal CreditToSalesRatioPercent { get; set; }
    public List<CreditNoteByCustomer> ByCustomer { get; set; } = new();
    public List<DailyCreditNote> DailyBreakdown { get; set; } = new();
    public List<CreditNoteByProduct> TopProductsReturned { get; set; } = new();
}

public class CreditNoteByCustomer
{
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public int CreditNoteCount { get; set; }
    public decimal TotalAmountUSD { get; set; }
    public decimal TotalAmountZIG { get; set; }
}

public class DailyCreditNote
{
    public DateTime Date { get; set; }
    public int Count { get; set; }
    public decimal TotalAmountUSD { get; set; }
    public decimal TotalAmountZIG { get; set; }
}

public class CreditNoteByProduct
{
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public decimal TotalQuantityReturned { get; set; }
    public decimal TotalCreditAmountUSD { get; set; }
    public decimal TotalCreditAmountZIG { get; set; }
    public int TimesReturned { get; set; }
}

#endregion

#region Purchase Orders Report

public class PurchaseOrderSummaryReport
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int TotalPurchaseOrders { get; set; }
    public int OpenOrders { get; set; }
    public int ClosedOrders { get; set; }
    public int CancelledOrders { get; set; }
    public decimal TotalOrderValueUSD { get; set; }
    public decimal TotalOrderValueZIG { get; set; }
    public decimal TotalPendingValueUSD { get; set; }
    public decimal TotalPendingValueZIG { get; set; }
    public decimal AverageOrderValueUSD { get; set; }
    public int UniqueSuppliers { get; set; }
    public List<PurchaseOrderBySupplier> BySupplier { get; set; } = new();
    public List<DailyPurchaseOrder> DailyBreakdown { get; set; } = new();
    public List<TopPurchasedProduct> TopProducts { get; set; } = new();
}

public class PurchaseOrderBySupplier
{
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public int OrderCount { get; set; }
    public decimal TotalValueUSD { get; set; }
    public decimal TotalValueZIG { get; set; }
    public int OpenOrders { get; set; }
    public decimal PendingValueUSD { get; set; }
}

public class DailyPurchaseOrder
{
    public DateTime Date { get; set; }
    public int Count { get; set; }
    public decimal TotalValueUSD { get; set; }
    public decimal TotalValueZIG { get; set; }
}

public class TopPurchasedProduct
{
    public int Rank { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public decimal TotalQuantityOrdered { get; set; }
    public decimal TotalCostUSD { get; set; }
    public decimal TotalCostZIG { get; set; }
    public int TimesOrdered { get; set; }
}

#endregion

#region Receivables Aging Report

public class ReceivablesAgingReport
{
    public DateTime ReportDate { get; set; }
    public int TotalCustomers { get; set; }
    public decimal TotalOutstandingUSD { get; set; }
    public decimal TotalOutstandingZIG { get; set; }
    public AgingBucket Current { get; set; } = new();
    public AgingBucket Days31To60 { get; set; } = new();
    public AgingBucket Days61To90 { get; set; } = new();
    public AgingBucket Over90Days { get; set; } = new();
    public List<CustomerAging> CustomerAging { get; set; } = new();
}

public class AgingBucket
{
    public string Label { get; set; } = string.Empty;
    public int InvoiceCount { get; set; }
    public decimal AmountUSD { get; set; }
    public decimal AmountZIG { get; set; }
    public decimal PercentOfTotal { get; set; }
}

public class CustomerAging
{
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public decimal CurrentUSD { get; set; }
    public decimal Days31To60USD { get; set; }
    public decimal Days61To90USD { get; set; }
    public decimal Over90DaysUSD { get; set; }
    public decimal TotalOutstandingUSD { get; set; }
    public decimal TotalOutstandingZIG { get; set; }
    public int TotalInvoices { get; set; }
}

#endregion

#region Profit Overview Report

public class ProfitOverviewReport
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public decimal TotalRevenueUSD { get; set; }
    public decimal TotalRevenueZIG { get; set; }
    public decimal TotalCreditNotesUSD { get; set; }
    public decimal TotalCreditNotesZIG { get; set; }
    public decimal NetRevenueUSD { get; set; }
    public decimal NetRevenueZIG { get; set; }
    public decimal TotalCollectedUSD { get; set; }
    public decimal TotalCollectedZIG { get; set; }
    public decimal CollectionRatePercent { get; set; }
    public decimal OutstandingReceivablesUSD { get; set; }
    public decimal OutstandingReceivablesZIG { get; set; }
    public decimal TotalVatUSD { get; set; }
    public decimal TotalVatZIG { get; set; }
    public decimal TotalPurchaseCostUSD { get; set; }
    public decimal TotalPurchaseCostZIG { get; set; }
    public decimal GrossProfitUSD { get; set; }
    public decimal GrossProfitZIG { get; set; }
    public decimal GrossMarginPercent { get; set; }
    public int TotalInvoices { get; set; }
    public int TotalCreditNoteCount { get; set; }
    public int TotalPayments { get; set; }
    public int UniqueCustomers { get; set; }
    public List<MonthlyProfit> MonthlyBreakdown { get; set; } = new();
}

public class MonthlyProfit
{
    public string Month { get; set; } = string.Empty;
    public decimal RevenueUSD { get; set; }
    public decimal RevenueZIG { get; set; }
    public decimal CreditNotesUSD { get; set; }
    public decimal CollectedUSD { get; set; }
    public decimal PurchaseCostUSD { get; set; }
    public decimal GrossProfitUSD { get; set; }
    public int InvoiceCount { get; set; }
    public int PaymentCount { get; set; }
}

#endregion

#region Slow Moving Products Report

public class SlowMovingProductsReport
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int DaysThreshold { get; set; }
    public List<SlowMovingProduct> Products { get; set; } = new();
}

public class SlowMovingProduct
{
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public decimal CurrentStock { get; set; }
    public DateTime? LastSoldDate { get; set; }
    public int DaysSinceLastSale { get; set; }
    public decimal StockValue { get; set; }
}

#endregion

#region User Management Models

public class UserListResponse
{
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<UserModel> Items { get; set; } = new();

    /// <summary>
    /// Alias for Items to maintain backwards compatibility
    /// </summary>
    public List<UserModel> Users => Items;
}

public class UserModel
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsLockedOut { get; set; }
    public bool IsLocked => IsLockedOut;
    public bool TwoFactorEnabled { get; set; }
    public List<string>? Permissions { get; set; }
    public List<string> AssignedWarehouseCodes { get; set; } = new();
    public string? AssignedWarehouseCode => AssignedWarehouseCodes.FirstOrDefault();
    public string? AssignedSection { get; set; }
    public List<string> AssignedCustomerCodes { get; set; } = new();
    public string? AssignedBusinessPartnerCode { get; set; }
    public string? AssignedCostCentreCode { get; set; }
    /// <summary>The depot a van loads from — the source of its stock transfer requests.</summary>
    public string? SupplyingWarehouseCode { get; set; }

    /// <summary>The selling route a van runs — the source of its territory and truck registration.</summary>
    public int? RouteId { get; set; }

    /// <summary>The ZIMRA fiscal device a van's handset signs as, or null if it stamps nothing itself.</summary>
    public int? FiscalDeviceId { get; set; }

    /// <summary>The shop a till operator works at — where its selling identity comes from.</summary>
    public int? ShopId { get; set; }

    /// <summary>The shop's code, carried so a list can show it without a second lookup.</summary>
    public string? ShopCode { get; set; }

    /// <summary>The shop's name, as an administrator would recognise it.</summary>
    public string? ShopName { get; set; }

    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public string TimeAgo => LastLoginAt.HasValue
        ? GetTimeAgo(DateTime.UtcNow - LastLoginAt.Value)
        : "Never";

    private static string GetTimeAgo(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        return $"{(int)(span.TotalDays / 7)}w ago";
    }
}

public class CreateUserRequest
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = "User";
}

public class UpdateUserRequest
{
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

#endregion

#region Notification Models

public class NotificationListResponse
{
    public int TotalCount { get; set; }
    public int UnreadCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<NotificationModel> Notifications { get; set; } = new();
}

/// <summary>
/// The <see cref="NotificationModel.Data"/> keys this app reads. The producer is
/// NotificationService in the ShopInventory API, which this project does not
/// reference — so these strings are one half of a contract the compiler cannot
/// check. Renaming a key there makes the toast fall back to its prose shape with
/// no error anywhere; keep the two sides in step by hand.
/// </summary>
public static class NotificationDataKeys
{
    public const string OrderNumber = "orderNumber";
    public const string CustomerName = "customerName";
    public const string DocTotal = "docTotal";
    public const string SourceLabel = "sourceLabel";
    public const string Source = "source";
    public const string CreatedBy = "createdBy";
}

public class NotificationModel
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? ActionUrl { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Structured fields the producer attached alongside the prose message —
    /// orderNumber, customerName, docTotal and the rest, for a sales order.
    /// Stored on the notification and returned by every read path, so it survives
    /// a page reload and the polling fallback. Still null for notification types
    /// whose producer attaches nothing, and for anything raised before the column
    /// was added, so consumers have to degrade.
    /// </summary>
    public Dictionary<string, string>? Data { get; set; }

    /// <summary>
    /// Reads one <see cref="Data"/> field, treating blank as absent. The API builds
    /// the dictionary case-insensitively but System.Text.Json rebuilds it with the
    /// default ordinal comparer, so a plain lookup would miss on any casing drift
    /// between the two sides — hence the fallback scan.
    /// </summary>
    public string? DataValue(string key)
    {
        if (Data is null)
        {
            return null;
        }

        if (Data.TryGetValue(key, out var exact))
        {
            return string.IsNullOrWhiteSpace(exact) ? null : exact;
        }

        foreach (var pair in Data)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(pair.Value) ? null : pair.Value;
            }
        }

        return null;
    }

    public string? EffectiveActionUrl =>
        string.Equals(Category, "SalesOrder", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ActionUrl, "/sales-orders", StringComparison.OrdinalIgnoreCase) &&
        Message.Contains("submitted from Mobile App", StringComparison.OrdinalIgnoreCase)
            ? "/mobile-drafts"
            : ActionUrl;

    public string TypeIconClass => Type switch
    {
        "Error" => "bi bi-x-circle-fill text-danger",
        "Warning" => "bi bi-exclamation-triangle-fill text-warning",
        "Success" => "bi bi-check-circle-fill text-success",
        "Info" => "bi bi-info-circle-fill text-info",
        _ => "bi bi-bell-fill"
    };

    public string TimeAgo
    {
        get
        {
            var diff = DateTime.UtcNow - CreatedAt;
            if (diff.TotalMinutes < 1) return "Just now";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
            return CreatedAt.ToString("MMM dd");
        }
    }

    /// <summary>
    /// What a notification does not already say in its title and message: the exact
    /// time behind "4h ago", the type its coloured mark stands for, what raised it,
    /// and whatever structured fields the producer attached. Both readers of a
    /// notification show these behind the same Details toggle — the /notifications
    /// page and the topbar bell — so the list is built once here.
    /// </summary>
    /// <remarks>
    /// <see cref="Data"/> is null for producers that attach nothing and for anything
    /// raised before the column existed, so this degrades to the fields every
    /// notification carries.
    /// </remarks>
    public List<NotificationDetailFact> DetailFacts()
    {
        var facts = new List<NotificationDetailFact>
        {
            new("Received", FormatTimestamp(CreatedAt)),
            new("Type", string.IsNullOrWhiteSpace(Type) ? "—" : Type)
        };

        if (!string.IsNullOrWhiteSpace(EntityType))
        {
            facts.Add(new NotificationDetailFact(
                "Reference",
                string.IsNullOrWhiteSpace(EntityId) ? EntityType : $"{EntityType} {EntityId}"));
        }

        if (!string.IsNullOrWhiteSpace(CreatedBy))
        {
            facts.Add(new NotificationDetailFact("Raised by", CreatedBy));
        }

        if (IsRead && ReadAt.HasValue)
        {
            facts.Add(new NotificationDetailFact("Read", FormatTimestamp(ReadAt.Value)));
        }

        if (Data is not null)
        {
            foreach (var pair in Data)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value))
                {
                    facts.Add(new NotificationDetailFact(HumaniseKey(pair.Key), FormatValue(pair.Key, pair.Value)));
                }
            }
        }

        return facts;
    }

    /// <summary>
    /// Timestamps arrive UTC — <see cref="TimeAgo"/> does its arithmetic against
    /// UtcNow on the same assumption — but System.Text.Json hands back an
    /// Unspecified Kind on a payload without an offset, and ToLocalTime() on
    /// Unspecified is a no-op that would silently show CAT times two hours early.
    /// State the Kind first.
    /// </summary>
    public static string FormatTimestamp(DateTime timestamp) =>
        DateTime.SpecifyKind(timestamp, DateTimeKind.Utc).ToLocalTime().ToString("d MMM yyyy, HH:mm");

    // Money fields are serialised straight off the SAP double, so a doc total
    // arrives as "1152.366600000000000" and would be shown raw. Round the amount
    // keys to 2dp; anything unparseable is left exactly as the producer sent it.
    private static string FormatValue(string key, string value)
    {
        if (!key.EndsWith("Total", StringComparison.OrdinalIgnoreCase) &&
            !key.EndsWith("Amount", StringComparison.OrdinalIgnoreCase) &&
            !key.EndsWith("Sum", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var amount)
            ? amount.ToString("N2", System.Globalization.CultureInfo.CurrentCulture)
            : value;
    }

    // Data keys are the producer's camelCase field names — see
    // NotificationDataKeys — so they are spaced and sentence-cased rather than
    // shown raw.
    private static string HumaniseKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        var label = new System.Text.StringBuilder(key.Length + 4);
        for (var i = 0; i < key.Length; i++)
        {
            if (i > 0 && char.IsUpper(key[i]) && !char.IsUpper(key[i - 1]))
            {
                label.Append(' ');
                label.Append(char.ToLowerInvariant(key[i]));
                continue;
            }

            label.Append(i == 0 ? char.ToUpperInvariant(key[i]) : key[i]);
        }

        return label.ToString();
    }
}

public sealed record NotificationDetailFact(string Label, string Value);

public class StockFetchProgressModel
{
    public int CompletedCount { get; set; }
    public int TotalCount { get; set; }
    public string CurrentWarehouse { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public List<string> CompletedWarehouses { get; set; } = new();
}

#endregion

#region Sync Status Models

public class SyncStatusDashboard
{
    public DateTime GeneratedAt { get; set; }
    public SapConnectionStatus SapConnection { get; set; } = new();
    public List<CacheSyncStatus> CacheStatuses { get; set; } = new();
    public OfflineQueueStatus OfflineQueue { get; set; } = new();
    public SyncHealthSummary HealthSummary { get; set; } = new();
}

public class SapConnectionStatus
{
    public bool IsConnected { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? LastConnectedAt { get; set; }
    public DateTime? LastErrorAt { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
    public double? ResponseTimeMs { get; set; }
    public string? CompanyDb { get; set; }

    public string StatusBadgeClass => Status switch
    {
        "Connected" => "badge bg-success",
        "Disabled" => "badge bg-secondary",
        _ => "badge bg-danger"
    };
}

public class CacheSyncStatus
{
    public string CacheKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime? LastSyncedAt { get; set; }
    public int ItemCount { get; set; }
    public bool IsStale { get; set; }
    public int StaleMinutes { get; set; }
    public string? LastError { get; set; }
    public string Status { get; set; } = string.Empty;

    public string StatusBadgeClass => Status switch
    {
        "Synced" => "badge bg-success",
        "Syncing" => "badge bg-info",
        "Stale" => "badge bg-warning",
        _ => "badge bg-danger"
    };
}

public class OfflineQueueStatus
{
    public int PendingCount { get; set; }
    public int FailedCount { get; set; }
    public int ProcessedCount { get; set; }
    public DateTime? OldestPendingAt { get; set; }
    public DateTime? LastProcessedAt { get; set; }
    public List<QueuedTransaction> PendingTransactions { get; set; } = new();
}

public class QueuedTransaction
{
    public int Id { get; set; }
    public string TransactionType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public string? CreatedBy { get; set; }
    public string? Summary { get; set; }

    public string StatusBadgeClass => Status switch
    {
        "Pending" => "badge bg-warning",
        "Processing" => "badge bg-info",
        "Completed" => "badge bg-success",
        "Failed" => "badge bg-danger",
        _ => "badge bg-secondary"
    };
}

public class SyncHealthSummary
{
    public string OverallHealth { get; set; } = string.Empty;
    public int HealthScore { get; set; }
    public List<string> Issues { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();

    public string HealthBadgeClass => OverallHealth switch
    {
        "Healthy" => "badge bg-success",
        "Warning" => "badge bg-warning",
        _ => "badge bg-danger"
    };
}

#endregion
