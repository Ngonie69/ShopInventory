using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Interface for reporting service
/// </summary>
public interface IReportService
{
    Task<SalesSummaryReportDto> GetSalesSummaryAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<TopProductsReportDto> GetTopProductsAsync(DateTime fromDate, DateTime toDate, int topCount = 10, string? warehouseCode = null, CancellationToken cancellationToken = default);
    Task<SlowMovingProductsReportDto> GetSlowMovingProductsAsync(DateTime fromDate, DateTime toDate, int daysThreshold = 30, CancellationToken cancellationToken = default);
    Task<StockSummaryReportDto> GetStockSummaryAsync(string? warehouseCode = null, CancellationToken cancellationToken = default);
    Task<StockMovementReportDto> GetStockMovementAsync(DateTime fromDate, DateTime toDate, string? warehouseCode = null, CancellationToken cancellationToken = default);
    Task<LowStockAlertReportDto> GetLowStockAlertsAsync(string? warehouseCode = null, decimal? reorderThreshold = null, CancellationToken cancellationToken = default);
    Task<PaymentSummaryReportDto> GetPaymentSummaryAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<TopCustomersReportDto> GetTopCustomersAsync(DateTime fromDate, DateTime toDate, int topCount = 10, CancellationToken cancellationToken = default);
}

/// <summary>
/// Report service implementation for generating business reports
/// </summary>
public class ReportService : IReportService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<ReportService> _logger;

    public ReportService(ApplicationDbContext context, ILogger<ReportService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get sales summary report for a date range
    /// </summary>
    public async Task<SalesSummaryReportDto> GetSalesSummaryAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating sales summary report from {FromDate} to {ToDate}", fromDate, toDate);

        var invoices = await _context.Invoices
            .Where(i => i.DocDate >= fromDate && i.DocDate <= toDate && i.Status == "Completed")
            .ToListAsync(cancellationToken);

        var usdInvoices = invoices.Where(i => i.DocCurrency == "USD" || string.IsNullOrEmpty(i.DocCurrency)).ToList();
        var zigInvoices = invoices.Where(i => i.DocCurrency == "ZIG").ToList();

        var dailySales = invoices
            .GroupBy(i => i.DocDate.Date)
            .Select(g => new DailySalesDto
            {
                Date = g.Key,
                InvoiceCount = g.Count(),
                TotalSalesUSD = g.Where(i => i.DocCurrency == "USD" || string.IsNullOrEmpty(i.DocCurrency)).Sum(i => i.DocTotal),
                TotalSalesZIG = g.Where(i => i.DocCurrency == "ZIG").Sum(i => i.DocTotal)
            })
            .OrderBy(d => d.Date)
            .ToList();

        var salesByCurrency = new List<SalesByCurrencyDto>
        {
            new() { Currency = "USD", InvoiceCount = usdInvoices.Count, TotalSales = usdInvoices.Sum(i => i.DocTotal), TotalVat = usdInvoices.Sum(i => i.VatSum) },
            new() { Currency = "ZIG", InvoiceCount = zigInvoices.Count, TotalSales = zigInvoices.Sum(i => i.DocTotal), TotalVat = zigInvoices.Sum(i => i.VatSum) }
        };

        return new SalesSummaryReportDto
        {
            FromDate = fromDate,
            ToDate = toDate,
            TotalInvoices = invoices.Count,
            TotalSalesUSD = usdInvoices.Sum(i => i.DocTotal),
            TotalSalesZIG = zigInvoices.Sum(i => i.DocTotal),
            TotalVatUSD = usdInvoices.Sum(i => i.VatSum),
            TotalVatZIG = zigInvoices.Sum(i => i.VatSum),
            AverageInvoiceValueUSD = usdInvoices.Count > 0 ? usdInvoices.Average(i => i.DocTotal) : 0,
            AverageInvoiceValueZIG = zigInvoices.Count > 0 ? zigInvoices.Average(i => i.DocTotal) : 0,
            UniqueCustomers = invoices.Select(i => i.CardCode).Distinct().Count(),
            DailySales = dailySales,
            SalesByCurrency = salesByCurrency
        };
    }

    /// <summary>
    /// Get top selling products report
    /// </summary>
    public async Task<TopProductsReportDto> GetTopProductsAsync(DateTime fromDate, DateTime toDate, int topCount = 10, string? warehouseCode = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating top products report from {FromDate} to {ToDate}, top {Count}", fromDate, toDate, topCount);

        var query = _context.InvoiceLines
            .Include(l => l.Invoice)
            .Where(l => l.Invoice != null && l.Invoice.DocDate >= fromDate && l.Invoice.DocDate <= toDate && l.Invoice.Status == "Completed");

        if (!string.IsNullOrEmpty(warehouseCode))
        {
            query = query.Where(l => l.WarehouseCode == warehouseCode);
        }

        var productSales = await query
            .GroupBy(l => new { l.ItemCode, l.ItemDescription })
            .Select(g => new
            {
                g.Key.ItemCode,
                ItemName = g.Key.ItemDescription ?? g.Key.ItemCode,
                TotalQuantity = g.Sum(l => l.Quantity),
                TotalRevenueUSD = g.Where(l => l.Invoice!.DocCurrency == "USD" || string.IsNullOrEmpty(l.Invoice.DocCurrency)).Sum(l => l.LineTotal),
                TotalRevenueZIG = g.Where(l => l.Invoice!.DocCurrency == "ZIG").Sum(l => l.LineTotal),
                TimesOrdered = g.Count()
            })
            .OrderByDescending(p => p.TotalQuantity)
            .Take(topCount)
            .ToListAsync(cancellationToken);

        var topProducts = productSales.Select((p, index) => new TopProductDto
        {
            Rank = index + 1,
            ItemCode = p.ItemCode ?? "Unknown",
            ItemName = p.ItemName ?? "Unknown",
            TotalQuantitySold = p.TotalQuantity,
            TotalRevenueUSD = p.TotalRevenueUSD,
            TotalRevenueZIG = p.TotalRevenueZIG,
            TimesOrdered = p.TimesOrdered
        }).ToList();

        return new TopProductsReportDto
        {
            FromDate = fromDate,
            ToDate = toDate,
            TotalProductsSold = topProducts.Count,
            TopProducts = topProducts
        };
    }

    /// <summary>
    /// Get slow moving products report
    /// </summary>
    public async Task<SlowMovingProductsReportDto> GetSlowMovingProductsAsync(DateTime fromDate, DateTime toDate, int daysThreshold = 30, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating slow moving products report, threshold {Days} days", daysThreshold);

        var products = await _context.Products
            .Where(p => p.IsActive && p.QuantityOnStock > 0)
            .ToListAsync(cancellationToken);

        var recentSales = await _context.InvoiceLines
            .Include(l => l.Invoice)
            .Where(l => l.Invoice != null && l.Invoice.DocDate >= fromDate && l.Invoice.Status == "Completed")
            .GroupBy(l => l.ItemCode)
            .Select(g => new { ItemCode = g.Key, LastSaleDate = g.Max(l => l.Invoice!.DocDate) })
            .ToListAsync(cancellationToken);

        var salesLookup = recentSales.ToDictionary(s => s.ItemCode ?? "", s => s.LastSaleDate);
        var today = DateTime.UtcNow;

        var slowMoving = products
            .Select(p => new SlowMovingProductDto
            {
                ItemCode = p.ItemCode,
                ItemName = p.ItemName ?? p.ItemCode,
                CurrentStock = p.QuantityOnStock,
                LastSoldDate = salesLookup.TryGetValue(p.ItemCode, out var lastSale) ? lastSale : null,
                DaysSinceLastSale = salesLookup.TryGetValue(p.ItemCode, out var ls) ? (int)(today - ls).TotalDays : 999,
                StockValue = p.QuantityOnStock * (p.Prices?.FirstOrDefault()?.Price ?? 0)
            })
            .Where(p => p.DaysSinceLastSale >= daysThreshold)
            .OrderByDescending(p => p.DaysSinceLastSale)
            .ToList();

        return new SlowMovingProductsReportDto
        {
            FromDate = fromDate,
            ToDate = toDate,
            DaysThreshold = daysThreshold,
            Products = slowMoving
        };
    }

    /// <summary>
    /// Get stock summary report
    /// </summary>
    public async Task<StockSummaryReportDto> GetStockSummaryAsync(string? warehouseCode = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating stock summary report for warehouse {Warehouse}", warehouseCode ?? "all");

        var products = await _context.Products
            .Include(p => p.Prices)
            .Include(p => p.Batches)
            .Where(p => p.IsActive)
            .ToListAsync(cancellationToken);

        if (!string.IsNullOrEmpty(warehouseCode))
        {
            products = products.Where(p => p.DefaultWarehouse == warehouseCode).ToList();
        }

        var stockByWarehouse = products
            .GroupBy(p => p.DefaultWarehouse ?? "Unknown")
            .Select(g => new StockByWarehouseDto
            {
                WarehouseCode = g.Key,
                WarehouseName = g.Key, // Would need warehouse lookup for name
                ProductCount = g.Count(),
                TotalQuantity = g.Sum(p => p.QuantityOnStock),
                TotalValueUSD = g.Sum(p => p.QuantityOnStock * (p.Prices?.FirstOrDefault(pr => pr.Currency == "USD")?.Price ?? 0)),
                TotalValueZIG = g.Sum(p => p.QuantityOnStock * (p.Prices?.FirstOrDefault(pr => pr.Currency == "ZIG")?.Price ?? 0))
            })
            .ToList();

        return new StockSummaryReportDto
        {
            ReportDate = DateTime.UtcNow,
            TotalProducts = products.Count,
            ProductsInStock = products.Count(p => p.QuantityOnStock > 0),
            ProductsOutOfStock = products.Count(p => p.QuantityOnStock <= 0),
            ProductsBelowReorderLevel = products.Count(p => p.QuantityOnStock < 10), // Default reorder level
            TotalStockValueUSD = products.Sum(p => p.QuantityOnStock * (p.Prices?.FirstOrDefault(pr => pr.Currency == "USD")?.Price ?? 0)),
            TotalStockValueZIG = products.Sum(p => p.QuantityOnStock * (p.Prices?.FirstOrDefault(pr => pr.Currency == "ZIG")?.Price ?? 0)),
            StockByWarehouse = stockByWarehouse
        };
    }

    /// <summary>
    /// Get stock movement report
    /// </summary>
    public async Task<StockMovementReportDto> GetStockMovementAsync(DateTime fromDate, DateTime toDate, string? warehouseCode = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating stock movement report from {FromDate} to {ToDate}", fromDate, toDate);

        var transfers = await _context.InventoryTransfers
            .Include(t => t.StockTransferLines)
            .Where(t => t.DocDate >= fromDate && t.DocDate <= toDate && t.Status == "Completed")
            .ToListAsync(cancellationToken);

        if (!string.IsNullOrEmpty(warehouseCode))
        {
            transfers = transfers.Where(t => t.FromWarehouse == warehouseCode || t.ToWarehouse == warehouseCode).ToList();
        }

        var movements = transfers.SelectMany(t => t.StockTransferLines.Select(l => new StockMovementDto
        {
            Date = t.DocDate,
            TransferType = "Transfer",
            ItemCode = l.ItemCode ?? "",
            ItemName = l.ItemDescription ?? "",
            Quantity = l.Quantity,
            FromWarehouse = t.FromWarehouse ?? "",
            ToWarehouse = t.ToWarehouse ?? "",
            Reference = $"T-{t.SAPDocNum}"
        })).OrderByDescending(m => m.Date).ToList();

        var warehouseFlows = transfers
            .SelectMany(t => new[]
            {
                new { Warehouse = t.FromWarehouse, Flow = -t.StockTransferLines.Sum(l => l.Quantity) },
                new { Warehouse = t.ToWarehouse, Flow = t.StockTransferLines.Sum(l => l.Quantity) }
            })
            .GroupBy(w => w.Warehouse)
            .Select(g => new WarehouseFlowDto
            {
                WarehouseCode = g.Key ?? "Unknown",
                WarehouseName = g.Key ?? "Unknown",
                TotalInflow = g.Where(w => w.Flow > 0).Sum(w => w.Flow),
                TotalOutflow = Math.Abs(g.Where(w => w.Flow < 0).Sum(w => w.Flow)),
                NetFlow = g.Sum(w => w.Flow),
                TransferCount = g.Count()
            })
            .ToList();

        return new StockMovementReportDto
        {
            FromDate = fromDate,
            ToDate = toDate,
            TotalTransfers = transfers.Count,
            TotalQuantityMoved = movements.Sum(m => m.Quantity),
            Movements = movements,
            WarehouseFlows = warehouseFlows
        };
    }

    /// <summary>
    /// Get low stock alerts
    /// </summary>
    public async Task<LowStockAlertReportDto> GetLowStockAlertsAsync(string? warehouseCode = null, decimal? reorderThreshold = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating low stock alerts for warehouse {Warehouse}", warehouseCode ?? "all");

        var threshold = reorderThreshold ?? 10m;

        var products = await _context.Products
            .Where(p => p.IsActive && p.QuantityOnStock < threshold)
            .ToListAsync(cancellationToken);

        if (!string.IsNullOrEmpty(warehouseCode))
        {
            products = products.Where(p => p.DefaultWarehouse == warehouseCode).ToList();
        }

        var items = products.Select(p => new LowStockItemDto
        {
            ItemCode = p.ItemCode,
            ItemName = p.ItemName ?? p.ItemCode,
            WarehouseCode = p.DefaultWarehouse ?? "Unknown",
            CurrentStock = p.QuantityOnStock,
            ReorderLevel = threshold,
            MinimumStock = 5,
            AlertLevel = p.QuantityOnStock <= 0 ? "Critical" : p.QuantityOnStock < 5 ? "Critical" : "Warning",
            SuggestedReorderQty = Math.Max(threshold * 2 - p.QuantityOnStock, 0)
        }).OrderBy(i => i.CurrentStock).ToList();

        return new LowStockAlertReportDto
        {
            ReportDate = DateTime.UtcNow,
            TotalAlerts = items.Count,
            CriticalCount = items.Count(i => i.AlertLevel == "Critical"),
            WarningCount = items.Count(i => i.AlertLevel == "Warning"),
            Items = items
        };
    }

    /// <summary>
    /// Get payment summary report
    /// </summary>
    public async Task<PaymentSummaryReportDto> GetPaymentSummaryAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating payment summary report from {FromDate} to {ToDate}", fromDate, toDate);

        var payments = await _context.IncomingPayments
            .Where(p => p.DocDate >= fromDate && p.DocDate <= toDate && p.Status == "Completed")
            .ToListAsync(cancellationToken);

        var usdPayments = payments.Where(p => p.DocCurrency == "USD" || string.IsNullOrEmpty(p.DocCurrency)).ToList();
        var zigPayments = payments.Where(p => p.DocCurrency == "ZIG").ToList();

        var paymentsByMethod = new List<PaymentByMethodDto>();
        var totalAmount = payments.Sum(p => p.DocTotal);

        // Cash payments
        var cashPayments = payments.Where(p => p.CashSum > 0).ToList();
        if (cashPayments.Any())
        {
            var cashTotal = cashPayments.Sum(p => p.CashSum);
            paymentsByMethod.Add(new PaymentByMethodDto
            {
                PaymentMethod = "Cash",
                PaymentCount = cashPayments.Count,
                TotalAmountUSD = cashPayments.Where(p => p.DocCurrency == "USD" || string.IsNullOrEmpty(p.DocCurrency)).Sum(p => p.CashSum),
                TotalAmountZIG = cashPayments.Where(p => p.DocCurrency == "ZIG").Sum(p => p.CashSum),
                PercentageOfTotal = totalAmount > 0 ? (cashTotal / totalAmount) * 100 : 0
            });
        }

        // Check payments
        var checkPayments = payments.Where(p => p.CheckSum > 0).ToList();
        if (checkPayments.Any())
        {
            var checkTotal = checkPayments.Sum(p => p.CheckSum);
            paymentsByMethod.Add(new PaymentByMethodDto
            {
                PaymentMethod = "Check",
                PaymentCount = checkPayments.Count,
                TotalAmountUSD = checkPayments.Where(p => p.DocCurrency == "USD" || string.IsNullOrEmpty(p.DocCurrency)).Sum(p => p.CheckSum),
                TotalAmountZIG = checkPayments.Where(p => p.DocCurrency == "ZIG").Sum(p => p.CheckSum),
                PercentageOfTotal = totalAmount > 0 ? (checkTotal / totalAmount) * 100 : 0
            });
        }

        // Bank Transfer payments
        var transferPayments = payments.Where(p => p.TransferSum > 0).ToList();
        if (transferPayments.Any())
        {
            var transferTotal = transferPayments.Sum(p => p.TransferSum);
            paymentsByMethod.Add(new PaymentByMethodDto
            {
                PaymentMethod = "Bank Transfer",
                PaymentCount = transferPayments.Count,
                TotalAmountUSD = transferPayments.Where(p => p.DocCurrency == "USD" || string.IsNullOrEmpty(p.DocCurrency)).Sum(p => p.TransferSum),
                TotalAmountZIG = transferPayments.Where(p => p.DocCurrency == "ZIG").Sum(p => p.TransferSum),
                PercentageOfTotal = totalAmount > 0 ? (transferTotal / totalAmount) * 100 : 0
            });
        }

        // Credit Card payments
        var creditPayments = payments.Where(p => p.CreditSum > 0).ToList();
        if (creditPayments.Any())
        {
            var creditTotal = creditPayments.Sum(p => p.CreditSum);
            paymentsByMethod.Add(new PaymentByMethodDto
            {
                PaymentMethod = "Credit Card",
                PaymentCount = creditPayments.Count,
                TotalAmountUSD = creditPayments.Where(p => p.DocCurrency == "USD" || string.IsNullOrEmpty(p.DocCurrency)).Sum(p => p.CreditSum),
                TotalAmountZIG = creditPayments.Where(p => p.DocCurrency == "ZIG").Sum(p => p.CreditSum),
                PercentageOfTotal = totalAmount > 0 ? (creditTotal / totalAmount) * 100 : 0
            });
        }

        var dailyPayments = payments
            .GroupBy(p => p.DocDate.Date)
            .Select(g => new DailyPaymentDto
            {
                Date = g.Key,
                PaymentCount = g.Count(),
                TotalAmountUSD = g.Where(p => p.DocCurrency == "USD" || string.IsNullOrEmpty(p.DocCurrency)).Sum(p => p.DocTotal),
                TotalAmountZIG = g.Where(p => p.DocCurrency == "ZIG").Sum(p => p.DocTotal)
            })
            .OrderBy(d => d.Date)
            .ToList();

        return new PaymentSummaryReportDto
        {
            FromDate = fromDate,
            ToDate = toDate,
            TotalPayments = payments.Count,
            TotalAmountUSD = usdPayments.Sum(p => p.DocTotal),
            TotalAmountZIG = zigPayments.Sum(p => p.DocTotal),
            PaymentsByMethod = paymentsByMethod,
            DailyPayments = dailyPayments
        };
    }

    /// <summary>
    /// Get top customers report
    /// </summary>
    public async Task<TopCustomersReportDto> GetTopCustomersAsync(DateTime fromDate, DateTime toDate, int topCount = 10, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating top customers report from {FromDate} to {ToDate}, top {Count}", fromDate, toDate, topCount);

        var invoices = await _context.Invoices
            .Where(i => i.DocDate >= fromDate && i.DocDate <= toDate && i.Status == "Completed")
            .ToListAsync(cancellationToken);

        var payments = await _context.IncomingPayments
            .Where(p => p.DocDate >= fromDate && p.DocDate <= toDate && p.Status == "Completed")
            .ToListAsync(cancellationToken);

        var customerInvoices = invoices
            .GroupBy(i => new { i.CardCode, i.CardName })
            .Select(g => new
            {
                g.Key.CardCode,
                g.Key.CardName,
                InvoiceCount = g.Count(),
                TotalPurchasesUSD = g.Where(i => i.DocCurrency == "USD" || string.IsNullOrEmpty(i.DocCurrency)).Sum(i => i.DocTotal),
                TotalPurchasesZIG = g.Where(i => i.DocCurrency == "ZIG").Sum(i => i.DocTotal)
            })
            .ToList();

        var customerPayments = payments
            .GroupBy(p => p.CardCode)
            .ToDictionary(
                g => g.Key ?? "",
                g => new
                {
                    TotalPaymentsUSD = g.Where(p => p.DocCurrency == "USD" || string.IsNullOrEmpty(p.DocCurrency)).Sum(p => p.DocTotal),
                    TotalPaymentsZIG = g.Where(p => p.DocCurrency == "ZIG").Sum(p => p.DocTotal)
                });

        var topCustomers = customerInvoices
            .OrderByDescending(c => c.TotalPurchasesUSD + c.TotalPurchasesZIG)
            .Take(topCount)
            .Select((c, index) =>
            {
                var paymentData = customerPayments.GetValueOrDefault(c.CardCode ?? "");
                return new TopCustomerDto
                {
                    Rank = index + 1,
                    CardCode = c.CardCode ?? "Unknown",
                    CardName = c.CardName ?? "Unknown",
                    InvoiceCount = c.InvoiceCount,
                    TotalPurchasesUSD = c.TotalPurchasesUSD,
                    TotalPurchasesZIG = c.TotalPurchasesZIG,
                    TotalPaymentsUSD = paymentData?.TotalPaymentsUSD ?? 0,
                    TotalPaymentsZIG = paymentData?.TotalPaymentsZIG ?? 0,
                    OutstandingBalanceUSD = c.TotalPurchasesUSD - (paymentData?.TotalPaymentsUSD ?? 0),
                    OutstandingBalanceZIG = c.TotalPurchasesZIG - (paymentData?.TotalPaymentsZIG ?? 0)
                };
            })
            .ToList();

        return new TopCustomersReportDto
        {
            FromDate = fromDate,
            ToDate = toDate,
            TotalCustomers = customerInvoices.Count,
            TopCustomers = topCustomers
        };
    }
}
