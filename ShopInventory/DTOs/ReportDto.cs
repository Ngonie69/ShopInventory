namespace ShopInventory.DTOs;

#region Sales Reports

/// <summary>
/// Sales summary report response
/// </summary>
public class SalesSummaryReportDto
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
    public List<DailySalesDto> DailySales { get; set; } = new();
    public List<SalesByCurrencyDto> SalesByCurrency { get; set; } = new();
}

/// <summary>
/// Daily sales breakdown
/// </summary>
public class DailySalesDto
{
    public DateTime Date { get; set; }
    public int InvoiceCount { get; set; }
    public decimal TotalSalesUSD { get; set; }
    public decimal TotalSalesZIG { get; set; }
}

/// <summary>
/// Sales grouped by currency
/// </summary>
public class SalesByCurrencyDto
{
    public string Currency { get; set; } = string.Empty;
    public int InvoiceCount { get; set; }
    public decimal TotalSales { get; set; }
    public decimal TotalVat { get; set; }
}

#endregion

#region Top Products Reports

/// <summary>
/// Top selling products report response
/// </summary>
public class TopProductsReportDto
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int TotalProductsSold { get; set; }
    public List<TopProductDto> TopProducts { get; set; } = new();
}

/// <summary>
/// Individual top product entry
/// </summary>
public class TopProductDto
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
/// Slow moving products report
/// </summary>
public class SlowMovingProductsReportDto
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int DaysThreshold { get; set; }
    public List<SlowMovingProductDto> Products { get; set; } = new();
}

/// <summary>
/// Individual slow moving product
/// </summary>
public class SlowMovingProductDto
{
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public decimal CurrentStock { get; set; }
    public DateTime? LastSoldDate { get; set; }
    public int DaysSinceLastSale { get; set; }
    public decimal StockValue { get; set; }
}

#endregion

#region Stock Reports

/// <summary>
/// Stock summary report response
/// </summary>
public class StockSummaryReportDto
{
    public DateTime ReportDate { get; set; }
    public int TotalProducts { get; set; }
    public int ProductsInStock { get; set; }
    public int ProductsOutOfStock { get; set; }
    public int ProductsBelowReorderLevel { get; set; }
    public decimal TotalStockValueUSD { get; set; }
    public decimal TotalStockValueZIG { get; set; }
    public List<StockByWarehouseDto> StockByWarehouse { get; set; } = new();
}

/// <summary>
/// Stock grouped by warehouse
/// </summary>
public class StockByWarehouseDto
{
    public string WarehouseCode { get; set; } = string.Empty;
    public string WarehouseName { get; set; } = string.Empty;
    public int ProductCount { get; set; }
    public decimal TotalQuantity { get; set; }
    public decimal TotalValueUSD { get; set; }
    public decimal TotalValueZIG { get; set; }
}

/// <summary>
/// Stock movement report response
/// </summary>
public class StockMovementReportDto
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int TotalTransfers { get; set; }
    public decimal TotalQuantityMoved { get; set; }
    public List<StockMovementDto> Movements { get; set; } = new();
    public List<WarehouseFlowDto> WarehouseFlows { get; set; } = new();
}

/// <summary>
/// Individual stock movement entry
/// </summary>
public class StockMovementDto
{
    public DateTime Date { get; set; }
    public string TransferType { get; set; } = string.Empty; // In, Out, Transfer
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string FromWarehouse { get; set; } = string.Empty;
    public string ToWarehouse { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
}

/// <summary>
/// Warehouse inflow/outflow summary
/// </summary>
public class WarehouseFlowDto
{
    public string WarehouseCode { get; set; } = string.Empty;
    public string WarehouseName { get; set; } = string.Empty;
    public decimal TotalInflow { get; set; }
    public decimal TotalOutflow { get; set; }
    public decimal NetFlow { get; set; }
    public int TransferCount { get; set; }
}

/// <summary>
/// Low stock alert report
/// </summary>
public class LowStockAlertReportDto
{
    public DateTime ReportDate { get; set; }
    public int TotalAlerts { get; set; }
    public int CriticalCount { get; set; }
    public int WarningCount { get; set; }
    public List<LowStockItemDto> Items { get; set; } = new();
}

/// <summary>
/// Individual low stock item
/// </summary>
public class LowStockItemDto
{
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string WarehouseCode { get; set; } = string.Empty;
    public decimal CurrentStock { get; set; }
    public decimal ReorderLevel { get; set; }
    public decimal MinimumStock { get; set; }
    public string AlertLevel { get; set; } = string.Empty; // Critical, Warning
    public decimal SuggestedReorderQty { get; set; }
}

#endregion

#region Payment Reports

/// <summary>
/// Payment summary report response
/// </summary>
public class PaymentSummaryReportDto
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int TotalPayments { get; set; }
    public decimal TotalAmountUSD { get; set; }
    public decimal TotalAmountZIG { get; set; }
    public List<PaymentByMethodDto> PaymentsByMethod { get; set; } = new();
    public List<DailyPaymentDto> DailyPayments { get; set; } = new();
}

/// <summary>
/// Payments grouped by method
/// </summary>
public class PaymentByMethodDto
{
    public string PaymentMethod { get; set; } = string.Empty; // Cash, Check, Credit Card, Bank Transfer
    public int PaymentCount { get; set; }
    public decimal TotalAmountUSD { get; set; }
    public decimal TotalAmountZIG { get; set; }
    public decimal PercentageOfTotal { get; set; }
}

/// <summary>
/// Daily payment breakdown
/// </summary>
public class DailyPaymentDto
{
    public DateTime Date { get; set; }
    public int PaymentCount { get; set; }
    public decimal TotalAmountUSD { get; set; }
    public decimal TotalAmountZIG { get; set; }
}

#endregion

#region Customer Reports

/// <summary>
/// Top customers report response
/// </summary>
public class TopCustomersReportDto
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int TotalCustomers { get; set; }
    public List<TopCustomerDto> TopCustomers { get; set; } = new();
}

/// <summary>
/// Individual top customer entry
/// </summary>
public class TopCustomerDto
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

#endregion

#region Order Fulfillment Reports

/// <summary>
/// Comprehensive order fulfillment report
/// </summary>
public class OrderFulfillmentReportDto
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
    public List<OrderFulfillmentItemDto> Orders { get; set; } = new();
    public List<FulfillmentByCustomerDto> FulfillmentByCustomer { get; set; } = new();
    public List<DailyFulfillmentDto> DailyFulfillment { get; set; } = new();
}

/// <summary>
/// Individual order fulfillment detail
/// </summary>
public class OrderFulfillmentItemDto
{
    public int DocNum { get; set; }
    public int DocEntry { get; set; }
    public DateTime OrderDate { get; set; }
    public DateTime DueDate { get; set; }
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public string DocCurrency { get; set; } = string.Empty;
    public decimal OrderTotal { get; set; }
    public string Status { get; set; } = string.Empty; // Open, Closed, Cancelled
    public int TotalLines { get; set; }
    public int DeliveredLines { get; set; }
    public decimal TotalQuantityOrdered { get; set; }
    public decimal TotalQuantityDelivered { get; set; }
    public decimal TotalQuantityPending { get; set; }
    public decimal FulfillmentPercent { get; set; }
    public bool IsOverdue { get; set; }
    public int DaysOverdue { get; set; }
    public List<OrderLineDetailDto> Lines { get; set; } = new();
}

/// <summary>
/// Order line fulfillment detail
/// </summary>
public class OrderLineDetailDto
{
    public string ItemCode { get; set; } = string.Empty;
    public string ItemDescription { get; set; } = string.Empty;
    public string WarehouseCode { get; set; } = string.Empty;
    public decimal QuantityOrdered { get; set; }
    public decimal QuantityDelivered { get; set; }
    public decimal QuantityPending { get; set; }
    public decimal LineTotal { get; set; }
    public string LineStatus { get; set; } = string.Empty; // Fulfilled, Partial, Pending
}

/// <summary>
/// Fulfillment grouped by customer
/// </summary>
public class FulfillmentByCustomerDto
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

/// <summary>
/// Daily fulfillment metrics
/// </summary>
public class DailyFulfillmentDto
{
    public DateTime Date { get; set; }
    public int OrdersPlaced { get; set; }
    public int OrdersClosed { get; set; }
    public decimal OrderValueUSD { get; set; }
    public decimal QuantityOrdered { get; set; }
    public decimal QuantityDelivered { get; set; }
}

#endregion

#region Report Request DTOs

/// <summary>
/// Date range request for reports
/// </summary>
public class DateRangeReportRequest
{
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
}

/// <summary>
/// Top products report request
/// </summary>
public class TopProductsReportRequest : DateRangeReportRequest
{
    public int TopCount { get; set; } = 10;
    public string? WarehouseCode { get; set; }
}

/// <summary>
/// Stock report request
/// </summary>
public class StockReportRequest
{
    public string? WarehouseCode { get; set; }
    public bool IncludeZeroStock { get; set; } = false;
}

/// <summary>
/// Low stock alert request
/// </summary>
public class LowStockAlertRequest
{
    public string? WarehouseCode { get; set; }
    public decimal? ReorderLevelThreshold { get; set; }
}

#endregion

#region Credit Notes Report

public class CreditNoteSummaryReportDto
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
    public List<CreditNoteByCustomerDto> ByCustomer { get; set; } = new();
    public List<DailyCreditNoteDto> DailyBreakdown { get; set; } = new();
    public List<CreditNoteByProductDto> TopProductsReturned { get; set; } = new();
}

public class CreditNoteByCustomerDto
{
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public int CreditNoteCount { get; set; }
    public decimal TotalAmountUSD { get; set; }
    public decimal TotalAmountZIG { get; set; }
}

public class DailyCreditNoteDto
{
    public DateTime Date { get; set; }
    public int Count { get; set; }
    public decimal TotalAmountUSD { get; set; }
    public decimal TotalAmountZIG { get; set; }
}

public class CreditNoteByProductDto
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

public class PurchaseOrderSummaryReportDto
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
    public List<PurchaseOrderBySupplierDto> BySupplier { get; set; } = new();
    public List<DailyPurchaseOrderDto> DailyBreakdown { get; set; } = new();
    public List<TopPurchasedProductDto> TopProducts { get; set; } = new();
}

public class PurchaseOrderBySupplierDto
{
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public int OrderCount { get; set; }
    public decimal TotalValueUSD { get; set; }
    public decimal TotalValueZIG { get; set; }
    public int OpenOrders { get; set; }
    public decimal PendingValueUSD { get; set; }
}

public class DailyPurchaseOrderDto
{
    public DateTime Date { get; set; }
    public int Count { get; set; }
    public decimal TotalValueUSD { get; set; }
    public decimal TotalValueZIG { get; set; }
}

public class TopPurchasedProductDto
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

public class ReceivablesAgingReportDto
{
    public DateTime ReportDate { get; set; }
    public int TotalCustomers { get; set; }
    public decimal TotalOutstandingUSD { get; set; }
    public decimal TotalOutstandingZIG { get; set; }
    public AgingBucketDto Current { get; set; } = new();
    public AgingBucketDto Days31To60 { get; set; } = new();
    public AgingBucketDto Days61To90 { get; set; } = new();
    public AgingBucketDto Over90Days { get; set; } = new();
    public List<CustomerAgingDto> CustomerAging { get; set; } = new();
}

public class AgingBucketDto
{
    public string Label { get; set; } = string.Empty;
    public int InvoiceCount { get; set; }
    public decimal AmountUSD { get; set; }
    public decimal AmountZIG { get; set; }
    public decimal PercentOfTotal { get; set; }
}

public class CustomerAgingDto
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

public class ProfitOverviewReportDto
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
    public List<MonthlyProfitDto> MonthlyBreakdown { get; set; } = new();
}

public class MonthlyProfitDto
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


