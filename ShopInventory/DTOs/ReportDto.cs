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
