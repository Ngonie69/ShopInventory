using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
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
    Task<OrderFulfillmentReportDto> GetOrderFulfillmentAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<CreditNoteSummaryReportDto> GetCreditNoteSummaryAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<PurchaseOrderSummaryReportDto> GetPurchaseOrderSummaryAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<ReceivablesAgingReportDto> GetReceivablesAgingAsync(CancellationToken cancellationToken = default);
    Task<ProfitOverviewReportDto> GetProfitOverviewAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
}

/// <summary>
/// Report service implementation that fetches live data from SAP Business One.
/// All reports query SAP directly via the Service Layer for real-time accuracy.
/// </summary>
public class ReportService : IReportService
{
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly ILogger<ReportService> _logger;

    public ReportService(ISAPServiceLayerClient sapClient, ILogger<ReportService> logger)
    {
        _sapClient = sapClient;
        _logger = logger;
    }

    /// <summary>
    /// Parse SAP date string (yyyy-MM-dd) to DateTime
    /// </summary>
    private static DateTime ParseSapDate(string? dateStr)
    {
        if (string.IsNullOrEmpty(dateStr)) return DateTime.MinValue;
        if (DateTime.TryParse(dateStr, out var dt)) return dt;
        return DateTime.MinValue;
    }

    /// <summary>
    /// Get sales summary report from SAP invoices
    /// </summary>
    public async Task<SalesSummaryReportDto> GetSalesSummaryAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating sales summary from SAP: {FromDate} to {ToDate}", fromDate, toDate);

        var invoices = await _sapClient.GetInvoicesByDateRangeAsync(fromDate, toDate, cancellationToken);

        var usdInvoices = invoices.Where(i => i.DocCurrency == "USD" || i.DocCurrency == "$" || string.IsNullOrEmpty(i.DocCurrency)).ToList();
        var zigInvoices = invoices.Where(i => i.DocCurrency == "ZIG" || i.DocCurrency == "ZiG").ToList();

        var dailySales = invoices
            .GroupBy(i => ParseSapDate(i.DocDate).Date)
            .Where(g => g.Key != DateTime.MinValue.Date)
            .Select(g => new DailySalesDto
            {
                Date = g.Key,
                InvoiceCount = g.Count(),
                TotalSalesUSD = g.Where(i => i.DocCurrency == "USD" || i.DocCurrency == "$" || string.IsNullOrEmpty(i.DocCurrency)).Sum(i => i.DocTotal),
                TotalSalesZIG = g.Where(i => i.DocCurrency == "ZIG" || i.DocCurrency == "ZiG").Sum(i => i.DocTotal)
            })
            .OrderBy(d => d.Date)
            .ToList();

        var salesByCurrency = new List<SalesByCurrencyDto>
        {
            new()
            {
                Currency = "USD",
                InvoiceCount = usdInvoices.Count,
                TotalSales = usdInvoices.Sum(i => i.DocTotal),
                TotalVat = usdInvoices.Sum(i => i.VatSum)
            },
            new()
            {
                Currency = "ZIG",
                InvoiceCount = zigInvoices.Count,
                TotalSales = zigInvoices.Sum(i => i.DocTotal),
                TotalVat = zigInvoices.Sum(i => i.VatSum)
            }
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
    /// Get top selling products from SAP invoice lines
    /// </summary>
    public async Task<TopProductsReportDto> GetTopProductsAsync(DateTime fromDate, DateTime toDate, int topCount = 10, string? warehouseCode = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating top products from SAP: {FromDate} to {ToDate}, top {Count}", fromDate, toDate, topCount);

        List<Invoice> invoices;
        if (!string.IsNullOrEmpty(warehouseCode))
        {
            invoices = await _sapClient.GetInvoicesByWarehouseAndDateRangeAsync(warehouseCode, fromDate, toDate, cancellationToken);
        }
        else
        {
            invoices = await _sapClient.GetInvoicesByDateRangeAsync(fromDate, toDate, cancellationToken);
        }

        // Flatten all invoice lines and filter in single enumeration
        var topProducts = invoices
            .Where(inv => inv.DocumentLines != null)
            .SelectMany(inv => inv.DocumentLines!.Select(line => new
            {
                Line = line,
                Currency = inv.DocCurrency
            }))
            .Where(l => string.IsNullOrEmpty(warehouseCode) || l.Line.WarehouseCode == warehouseCode)
            .GroupBy(l => new { l.Line.ItemCode, l.Line.ItemDescription })
            .Select(g => new
            {
                ItemCode = g.Key.ItemCode ?? "Unknown",
                ItemName = g.Key.ItemDescription ?? g.Key.ItemCode ?? "Unknown",
                TotalQuantity = g.Sum(l => l.Line.Quantity),
                TotalRevenueUSD = g.Where(l => l.Currency == "USD" || l.Currency == "$" || string.IsNullOrEmpty(l.Currency)).Sum(l => l.Line.LineTotal),
                TotalRevenueZIG = g.Where(l => l.Currency == "ZIG" || l.Currency == "ZiG").Sum(l => l.Line.LineTotal),
                TimesOrdered = g.Count()
            })
            .OrderByDescending(p => p.TotalQuantity)
            .Take(topCount)
            .Select((p, index) => new TopProductDto
            {
                Rank = index + 1,
                ItemCode = p.ItemCode,
                ItemName = p.ItemName,
                TotalQuantitySold = p.TotalQuantity,
                TotalRevenueUSD = p.TotalRevenueUSD,
                TotalRevenueZIG = p.TotalRevenueZIG,
                TimesOrdered = p.TimesOrdered
            })
            .ToList();

        return new TopProductsReportDto
        {
            FromDate = fromDate,
            ToDate = toDate,
            TotalProductsSold = topProducts.Count,
            TopProducts = topProducts
        };
    }

    /// <summary>
    /// Get slow moving products by comparing SAP stock with recent sales
    /// </summary>
    public async Task<SlowMovingProductsReportDto> GetSlowMovingProductsAsync(DateTime fromDate, DateTime toDate, int daysThreshold = 30, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating slow moving products from SAP, threshold {Days} days", daysThreshold);

        // Get all items with stock from SAP
        var items = await _sapClient.GetAllItemsAsync(cancellationToken);
        var activeItems = items.Where(i => i.QuantityOnStock > 0).ToList();

        // Get invoices for the period to find last sale dates
        var invoices = await _sapClient.GetInvoicesByDateRangeAsync(fromDate, toDate, cancellationToken);

        var lastSaleLookup = invoices
            .Where(inv => inv.DocumentLines is not null)
            .SelectMany(inv => inv.DocumentLines!.Select(line => new
            {
                line.ItemCode,
                DocDate = ParseSapDate(inv.DocDate)
            }))
            .GroupBy(l => l.ItemCode)
            .ToDictionary(
                g => g.Key ?? "",
                g => g.Max(l => l.DocDate)
            );

        var today = DateTime.UtcNow;
        var slowMoving = activeItems
            .Where(item => item.QuantityOnStock > 0)
            .Select(item => new SlowMovingProductDto
            {
                ItemCode = item.ItemCode ?? "Unknown",
                ItemName = item.ItemName ?? item.ItemCode ?? "Unknown",
                CurrentStock = item.QuantityOnStock,
                LastSoldDate = lastSaleLookup.TryGetValue(item.ItemCode ?? "", out var lastSale) ? lastSale : null,
                DaysSinceLastSale = lastSaleLookup.TryGetValue(item.ItemCode ?? "", out var ls) && ls != DateTime.MinValue
                    ? (int)(today - ls).TotalDays : 999,
                StockValue = 0 // Would need price list lookup
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
    /// Get stock summary from SAP warehouses (parallelized)
    /// </summary>
    public async Task<StockSummaryReportDto> GetStockSummaryAsync(string? warehouseCode = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating stock summary from SAP for warehouse {Warehouse}", warehouseCode ?? "all");

        var warehouses = await _sapClient.GetWarehousesAsync(cancellationToken);
        var activeWarehouses = warehouses.Where(w => w.IsActive).ToList();

        if (!string.IsNullOrEmpty(warehouseCode))
        {
            activeWarehouses = activeWarehouses.Where(w => w.WarehouseCode == warehouseCode).ToList();
        }

        var results = new System.Collections.Concurrent.ConcurrentBag<(StockByWarehouseDto Dto, int InStock, int OutOfStock, int BelowReorder)>();
        var semaphore = new SemaphoreSlim(3); // Max 3 concurrent per report to avoid saturating global SAP semaphore

        var tasks = activeWarehouses.Select(async wh =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var stockItems = await _sapClient.GetStockQuantitiesInWarehouseAsync(
                    wh.WarehouseCode!, cancellationToken);

                results.Add((new StockByWarehouseDto
                {
                    WarehouseCode = wh.WarehouseCode ?? "Unknown",
                    WarehouseName = wh.WarehouseName ?? wh.WarehouseCode ?? "Unknown",
                    ProductCount = stockItems.Count,
                    TotalQuantity = stockItems.Sum(s => s.InStock)
                },
                stockItems.Count(s => s.InStock > 0),
                stockItems.Count(s => s.InStock <= 0),
                stockItems.Count(s => s.InStock > 0 && s.InStock < 10)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get stock for warehouse {Wh}", wh.WarehouseCode);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        var resultList = results.ToList();
        var stockByWarehouse = resultList.Select(r => r.Dto).OrderBy(s => s.WarehouseCode).ToList();

        return new StockSummaryReportDto
        {
            ReportDate = DateTime.UtcNow,
            TotalProducts = resultList.Sum(r => r.Dto.ProductCount),
            ProductsInStock = resultList.Sum(r => r.InStock),
            ProductsOutOfStock = resultList.Sum(r => r.OutOfStock),
            ProductsBelowReorderLevel = resultList.Sum(r => r.BelowReorder),
            TotalStockValueUSD = 0,
            TotalStockValueZIG = 0,
            StockByWarehouse = stockByWarehouse
        };
    }

    /// <summary>
    /// Get stock movement report from SAP inventory transfers
    /// </summary>
    public async Task<StockMovementReportDto> GetStockMovementAsync(DateTime fromDate, DateTime toDate, string? warehouseCode = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating stock movement from SAP: {FromDate} to {ToDate}", fromDate, toDate);

        // If warehouse specified, use warehouse-specific query; otherwise get all
        List<InventoryTransfer> transfers;
        if (!string.IsNullOrEmpty(warehouseCode))
        {
            transfers = await _sapClient.GetInventoryTransfersByDateRangeAsync(warehouseCode, fromDate, toDate, cancellationToken);
        }
        else
        {
            // Get transfers for all active warehouses (use first warehouse approach or get all)
            // The SAP client requires a warehouse code, so we'll get all and filter client-side
            var warehouses = await _sapClient.GetWarehousesAsync(cancellationToken);
            var activeWhs = warehouses.Where(w => w.IsActive).Select(w => w.WarehouseCode!).ToList();

            transfers = new List<InventoryTransfer>();
            var seen = new HashSet<int>();
            foreach (var wh in activeWhs.Take(10)) // Limit to avoid too many SAP calls
            {
                try
                {
                    var whTransfers = await _sapClient.GetInventoryTransfersByDateRangeAsync(wh, fromDate, toDate, cancellationToken);
                    foreach (var t in whTransfers)
                    {
                        if (seen.Add(t.DocEntry))
                            transfers.Add(t);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get transfers for warehouse {Wh}", wh);
                }
            }
        }

        var movements = transfers
            .Where(t => t.StockTransferLines != null)
            .SelectMany(t => t.StockTransferLines!.Select(l => new StockMovementDto
            {
                Date = ParseSapDate(t.DocDate),
                TransferType = "Transfer",
                ItemCode = l.ItemCode ?? "",
                ItemName = l.ItemDescription ?? "",
                Quantity = l.Quantity,
                FromWarehouse = l.FromWarehouseCode ?? t.FromWarehouse ?? "",
                ToWarehouse = l.WarehouseCode ?? t.ToWarehouse ?? "",
                Reference = $"T-{t.DocNum}"
            }))
            .OrderByDescending(m => m.Date)
            .ToList();

        var warehouseFlows = movements
            .SelectMany(m => new[]
            {
                new { Warehouse = m.FromWarehouse, Flow = -m.Quantity },
                new { Warehouse = m.ToWarehouse, Flow = m.Quantity }
            })
            .Where(w => !string.IsNullOrEmpty(w.Warehouse))
            .GroupBy(w => w.Warehouse)
            .Select(g => new WarehouseFlowDto
            {
                WarehouseCode = g.Key,
                WarehouseName = g.Key,
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
    /// Get low stock alerts from SAP (parallelized)
    /// </summary>
    public async Task<LowStockAlertReportDto> GetLowStockAlertsAsync(string? warehouseCode = null, decimal? reorderThreshold = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating low stock alerts from SAP for warehouse {Warehouse}", warehouseCode ?? "all");

        var threshold = reorderThreshold ?? 10m;
        var allItems = new System.Collections.Concurrent.ConcurrentBag<LowStockItemDto>();

        var warehouses = await _sapClient.GetWarehousesAsync(cancellationToken);
        var targetWarehouses = warehouses.Where(w => w.IsActive).ToList();

        if (!string.IsNullOrEmpty(warehouseCode))
        {
            targetWarehouses = targetWarehouses.Where(w => w.WarehouseCode == warehouseCode).ToList();
        }

        var semaphore = new SemaphoreSlim(3);
        var tasks = targetWarehouses.Select(async wh =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var stockItems = await _sapClient.GetStockQuantitiesInWarehouseAsync(
                    wh.WarehouseCode!, cancellationToken);

                foreach (var s in stockItems.Where(s => s.InStock < threshold && s.InStock >= 0))
                {
                    allItems.Add(new LowStockItemDto
                    {
                        ItemCode = s.ItemCode ?? "Unknown",
                        ItemName = s.ItemName ?? s.ItemCode ?? "Unknown",
                        WarehouseCode = wh.WarehouseCode ?? "Unknown",
                        CurrentStock = s.InStock,
                        ReorderLevel = threshold,
                        MinimumStock = 5,
                        AlertLevel = s.InStock <= 0 ? "Critical" : s.InStock < 5 ? "Critical" : "Warning",
                        SuggestedReorderQty = Math.Max(threshold * 2 - s.InStock, 0)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get stock for warehouse {Wh}", wh.WarehouseCode);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        var items = allItems.OrderBy(i => i.CurrentStock).ToList();

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
    /// Get payment summary from SAP incoming payments
    /// </summary>
    public async Task<PaymentSummaryReportDto> GetPaymentSummaryAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating payment summary from SAP: {FromDate} to {ToDate}", fromDate, toDate);

        var payments = await _sapClient.GetIncomingPaymentsByDateRangeAsync(fromDate, toDate, cancellationToken);

        // Helper: compute effective payment amount from method sums (more reliable than DocTotal in SAP)
        static decimal GetPaymentTotal(IncomingPayment p) =>
            p.CashSum + p.CheckSum + p.TransferSum + p.CreditSum;

        var usdPayments = payments.Where(p => p.DocCurrency == "USD" || p.DocCurrency == "$" || string.IsNullOrEmpty(p.DocCurrency)).ToList();
        var zigPayments = payments.Where(p => p.DocCurrency == "ZIG" || p.DocCurrency == "ZiG").ToList();

        var paymentsByMethod = new List<PaymentByMethodDto>();
        var totalAmount = payments.Sum(p => GetPaymentTotal(p));

        // Cash payments
        var cashPayments = payments.Where(p => p.CashSum > 0).ToList();
        if (cashPayments.Any())
        {
            var cashTotal = cashPayments.Sum(p => p.CashSum);
            paymentsByMethod.Add(new PaymentByMethodDto
            {
                PaymentMethod = "Cash",
                PaymentCount = cashPayments.Count,
                TotalAmountUSD = cashPayments.Where(p => p.DocCurrency == "USD" || p.DocCurrency == "$" || string.IsNullOrEmpty(p.DocCurrency)).Sum(p => p.CashSum),
                TotalAmountZIG = cashPayments.Where(p => p.DocCurrency == "ZIG" || p.DocCurrency == "ZiG").Sum(p => p.CashSum),
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
                TotalAmountUSD = checkPayments.Where(p => p.DocCurrency == "USD" || p.DocCurrency == "$" || string.IsNullOrEmpty(p.DocCurrency)).Sum(p => p.CheckSum),
                TotalAmountZIG = checkPayments.Where(p => p.DocCurrency == "ZIG" || p.DocCurrency == "ZiG").Sum(p => p.CheckSum),
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
                TotalAmountUSD = transferPayments.Where(p => p.DocCurrency == "USD" || p.DocCurrency == "$" || string.IsNullOrEmpty(p.DocCurrency)).Sum(p => p.TransferSum),
                TotalAmountZIG = transferPayments.Where(p => p.DocCurrency == "ZIG" || p.DocCurrency == "ZiG").Sum(p => p.TransferSum),
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
                TotalAmountUSD = creditPayments.Where(p => p.DocCurrency == "USD" || p.DocCurrency == "$" || string.IsNullOrEmpty(p.DocCurrency)).Sum(p => p.CreditSum),
                TotalAmountZIG = creditPayments.Where(p => p.DocCurrency == "ZIG" || p.DocCurrency == "ZiG").Sum(p => p.CreditSum),
                PercentageOfTotal = totalAmount > 0 ? (creditTotal / totalAmount) * 100 : 0
            });
        }

        var dailyPayments = payments
            .GroupBy(p => ParseSapDate(p.DocDate).Date)
            .Where(g => g.Key != DateTime.MinValue.Date)
            .Select(g => new DailyPaymentDto
            {
                Date = g.Key,
                PaymentCount = g.Count(),
                TotalAmountUSD = g.Where(p => p.DocCurrency == "USD" || p.DocCurrency == "$" || string.IsNullOrEmpty(p.DocCurrency)).Sum(p => GetPaymentTotal(p)),
                TotalAmountZIG = g.Where(p => p.DocCurrency == "ZIG" || p.DocCurrency == "ZiG").Sum(p => GetPaymentTotal(p))
            })
            .OrderBy(d => d.Date)
            .ToList();

        return new PaymentSummaryReportDto
        {
            FromDate = fromDate,
            ToDate = toDate,
            TotalPayments = payments.Count,
            TotalAmountUSD = usdPayments.Sum(p => GetPaymentTotal(p)),
            TotalAmountZIG = zigPayments.Sum(p => GetPaymentTotal(p)),
            PaymentsByMethod = paymentsByMethod,
            DailyPayments = dailyPayments
        };
    }

    /// <summary>
    /// Get top customers from SAP invoices and payments
    /// </summary>
    public async Task<TopCustomersReportDto> GetTopCustomersAsync(DateTime fromDate, DateTime toDate, int topCount = 10, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating top customers from SAP: {FromDate} to {ToDate}, top {Count}", fromDate, toDate, topCount);

        var invoices = await _sapClient.GetInvoicesByDateRangeAsync(fromDate, toDate, cancellationToken);
        var payments = await _sapClient.GetIncomingPaymentsByDateRangeAsync(fromDate, toDate, cancellationToken);

        var customerInvoices = invoices
            .GroupBy(i => new { i.CardCode, i.CardName })
            .Select(g => new
            {
                CardCode = g.Key.CardCode ?? "Unknown",
                CardName = g.Key.CardName ?? g.Key.CardCode ?? "Unknown",
                InvoiceCount = g.Count(),
                TotalPurchasesUSD = g.Where(i => i.DocCurrency == "USD" || i.DocCurrency == "$" || string.IsNullOrEmpty(i.DocCurrency)).Sum(i => i.DocTotal),
                TotalPurchasesZIG = g.Where(i => i.DocCurrency == "ZIG" || i.DocCurrency == "ZiG").Sum(i => i.DocTotal)
            })
            .ToList();

        var customerPayments = payments
            .GroupBy(p => p.CardCode)
            .ToDictionary(
                g => g.Key ?? "",
                g => new
                {
                    TotalPaymentsUSD = g.Where(p => p.DocCurrency == "USD" || p.DocCurrency == "$" || string.IsNullOrEmpty(p.DocCurrency)).Sum(p => p.DocTotal),
                    TotalPaymentsZIG = g.Where(p => p.DocCurrency == "ZIG" || p.DocCurrency == "ZiG").Sum(p => p.DocTotal)
                });

        var topCustomers = customerInvoices
            .OrderByDescending(c => c.TotalPurchasesUSD + c.TotalPurchasesZIG)
            .Take(topCount)
            .Select((c, index) =>
            {
                var paymentData = customerPayments.GetValueOrDefault(c.CardCode);
                return new TopCustomerDto
                {
                    Rank = index + 1,
                    CardCode = c.CardCode,
                    CardName = c.CardName,
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

    /// <summary>
    /// Get comprehensive order fulfillment report from SAP sales orders
    /// </summary>
    public async Task<OrderFulfillmentReportDto> GetOrderFulfillmentAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating order fulfillment report via SQL from SAP: {FromDate} to {ToDate}", fromDate, toDate);

        var fromStr = fromDate.ToString("yyyyMMdd");
        var toStr = toDate.ToString("yyyyMMdd");

        // 1) Summary by customer + status + currency — aggregated, far fewer rows than individual orders
        var customerSql = $@"SELECT T0.""CardCode"", T0.""CardName"", T0.""DocCur"", T0.""DocStatus"", T0.""CANCELED"", COUNT(T0.""DocEntry"") AS ""OrderCount"", SUM(T0.""DocTotal"") AS ""TotalValue"" FROM ORDR T0 WHERE T0.""DocDate"" BETWEEN '{fromStr}' AND '{toStr}' GROUP BY T0.""CardCode"", T0.""CardName"", T0.""DocCur"", T0.""DocStatus"", T0.""CANCELED"" ORDER BY T0.""CardName""";

        // 2) Daily summary — one row per date+status+currency
        var dailySql = $@"SELECT T0.""DocDate"", T0.""DocCur"", T0.""DocStatus"", T0.""CANCELED"", COUNT(T0.""DocEntry"") AS ""OrderCount"", SUM(T0.""DocTotal"") AS ""TotalValue"" FROM ORDR T0 WHERE T0.""DocDate"" BETWEEN '{fromStr}' AND '{toStr}' GROUP BY T0.""DocDate"", T0.""DocCur"", T0.""DocStatus"", T0.""CANCELED"" ORDER BY T0.""DocDate""";

        // Execute both queries sequentially (SAP Session cannot handle parallel)
        var customerRows = await _sapClient.ExecuteRawSqlQueryAsync("ORD_FULF_CUST", "Fulfillment By Customer", customerSql, cancellationToken);
        var dailyRows = await _sapClient.ExecuteRawSqlQueryAsync("ORD_FULF_DAILY", "Fulfillment Daily", dailySql, cancellationToken);

        // Process customer aggregation into totals and by-customer breakdown
        int totalOrders = 0, openOrders = 0, closedOrders = 0, cancelledOrders = 0;
        decimal totalUSD = 0, totalZIG = 0, closedUSD = 0, closedZIG = 0, openUSD = 0, openZIG = 0;

        var customerAgg = new Dictionary<string, (string CardName, int Total, int Open, int Closed, decimal TotalValue, decimal PendingValue)>();

        foreach (var row in customerRows)
        {
            var docStatus = row.GetValueOrDefault("DocStatus")?.ToString() ?? "";
            var cancelled = row.GetValueOrDefault("CANCELED")?.ToString() ?? "";
            var isCancelled = cancelled == "Y";
            var isClosed = docStatus == "C";
            var status = isCancelled ? "Cancelled" : isClosed ? "Closed" : "Open";

            var count = Convert.ToInt32(row.GetValueOrDefault("OrderCount") ?? 0);
            var value = Convert.ToDecimal(row.GetValueOrDefault("TotalValue") ?? 0);
            var currency = row.GetValueOrDefault("DocCur")?.ToString() ?? "USD";
            var isUSD = currency == "USD" || currency == "$" || string.IsNullOrEmpty(currency);
            var isZIG = currency == "ZIG" || currency == "ZiG";

            totalOrders += count;
            if (isCancelled) cancelledOrders += count;
            else if (isClosed) { closedOrders += count; if (isUSD) closedUSD += value; if (isZIG) closedZIG += value; }
            else { openOrders += count; if (isUSD) openUSD += value; if (isZIG) openZIG += value; }

            if (isUSD) totalUSD += value;
            if (isZIG) totalZIG += value;

            // Aggregate by customer (skip cancelled)
            if (!isCancelled)
            {
                var cardCode = row.GetValueOrDefault("CardCode")?.ToString() ?? "Unknown";
                var cardName = row.GetValueOrDefault("CardName")?.ToString() ?? cardCode;

                if (!customerAgg.TryGetValue(cardCode, out var existing))
                    existing = (cardName, 0, 0, 0, 0, 0);

                customerAgg[cardCode] = (
                    cardName,
                    existing.Total + count,
                    existing.Open + (isClosed ? 0 : count),
                    existing.Closed + (isClosed ? count : 0),
                    existing.TotalValue + value,
                    existing.PendingValue + (isClosed ? 0 : value)
                );
            }
        }

        var nonCancelledCount = openOrders + closedOrders;
        var overallFulfillment = nonCancelledCount > 0 ? (decimal)closedOrders / nonCancelledCount * 100 : 0;
        var usdOrderCount = customerRows
            .Where(r => { var c = r.GetValueOrDefault("DocCur")?.ToString() ?? ""; return c == "USD" || c == "$" || string.IsNullOrEmpty(c); })
            .Sum(r => Convert.ToInt32(r.GetValueOrDefault("OrderCount") ?? 0));
        var avgUSD = usdOrderCount > 0 ? totalUSD / usdOrderCount : 0;

        // Build customer list
        var byCustomer = customerAgg
            .Select(kvp => new FulfillmentByCustomerDto
            {
                CardCode = kvp.Key,
                CardName = kvp.Value.CardName,
                TotalOrders = kvp.Value.Total,
                OpenOrders = kvp.Value.Open,
                ClosedOrders = kvp.Value.Closed,
                TotalOrderValue = kvp.Value.TotalValue,
                FulfillmentRatePercent = kvp.Value.Total > 0 ? Math.Round((decimal)kvp.Value.Closed / kvp.Value.Total * 100, 1) : 0,
                TotalPendingValue = kvp.Value.PendingValue
            })
            .OrderByDescending(c => c.TotalOrders)
            .ToList();

        // Process daily data
        var dailyAgg = new Dictionary<DateTime, (int Placed, int Closed, decimal ValueUSD)>();
        foreach (var row in dailyRows)
        {
            var date = ParseSapDate(row.GetValueOrDefault("DocDate")?.ToString());
            if (date == DateTime.MinValue) continue;
            var dateKey = date.Date;

            var docStatus = row.GetValueOrDefault("DocStatus")?.ToString() ?? "";
            var cancelled = row.GetValueOrDefault("CANCELED")?.ToString() ?? "";
            var isClosed = docStatus == "C" && cancelled != "Y";
            var count = Convert.ToInt32(row.GetValueOrDefault("OrderCount") ?? 0);
            var value = Convert.ToDecimal(row.GetValueOrDefault("TotalValue") ?? 0);
            var currency = row.GetValueOrDefault("DocCur")?.ToString() ?? "USD";
            var isUSD = currency == "USD" || currency == "$" || string.IsNullOrEmpty(currency);

            if (!dailyAgg.TryGetValue(dateKey, out var existing))
                existing = (0, 0, 0);

            dailyAgg[dateKey] = (
                existing.Placed + count,
                existing.Closed + (isClosed ? count : 0),
                existing.ValueUSD + (isUSD ? value : 0)
            );
        }

        var daily = dailyAgg
            .Select(kvp => new DailyFulfillmentDto
            {
                Date = kvp.Key,
                OrdersPlaced = kvp.Value.Placed,
                OrdersClosed = kvp.Value.Closed,
                OrderValueUSD = kvp.Value.ValueUSD,
                QuantityOrdered = 0,
                QuantityDelivered = 0
            })
            .OrderBy(d => d.Date)
            .ToList();

        // No individual order list — aggregated data only for performance
        var orderItems = new List<OrderFulfillmentItemDto>();

        return new OrderFulfillmentReportDto
        {
            FromDate = fromDate,
            ToDate = toDate,
            TotalOrders = totalOrders,
            OpenOrders = openOrders,
            ClosedOrders = closedOrders,
            CancelledOrders = cancelledOrders,
            FulfillmentRatePercent = Math.Round(overallFulfillment, 1),
            TotalOrderValueUSD = totalUSD,
            TotalOrderValueZIG = totalZIG,
            TotalDeliveredValueUSD = closedUSD,
            TotalDeliveredValueZIG = closedZIG,
            TotalPendingValueUSD = openUSD,
            TotalPendingValueZIG = openZIG,
            AverageOrderValueUSD = Math.Round(avgUSD, 2),
            TotalLineItems = closedOrders + openOrders,
            FullyDeliveredLines = closedOrders,
            PartiallyDeliveredLines = 0,
            UndeliveredLines = openOrders,
            Orders = orderItems,
            FulfillmentByCustomer = byCustomer,
            DailyFulfillment = daily
        };
    }

    /// <summary>
    /// Get credit notes summary from SAP
    /// </summary>
    public async Task<CreditNoteSummaryReportDto> GetCreditNoteSummaryAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating credit note summary from SAP: {FromDate} to {ToDate}", fromDate, toDate);

        var creditNotes = await _sapClient.GetCreditNotesByDateRangeAsync(fromDate, toDate, cancellationToken);
        // Exclude cancelled credit notes
        creditNotes = creditNotes.Where(cn => cn.Cancelled != "tYES").ToList();

        var usdCNs = creditNotes.Where(cn => cn.DocCurrency == "USD" || cn.DocCurrency == "$" || string.IsNullOrEmpty(cn.DocCurrency)).ToList();
        var zigCNs = creditNotes.Where(cn => cn.DocCurrency == "ZIG" || cn.DocCurrency == "ZiG").ToList();

        // By customer breakdown
        var byCustomer = creditNotes
            .GroupBy(cn => new { cn.CardCode, cn.CardName })
            .Select(g => new CreditNoteByCustomerDto
            {
                CardCode = g.Key.CardCode ?? "Unknown",
                CardName = g.Key.CardName ?? g.Key.CardCode ?? "Unknown",
                CreditNoteCount = g.Count(),
                TotalAmountUSD = g.Where(cn => cn.DocCurrency == "USD" || cn.DocCurrency == "$" || string.IsNullOrEmpty(cn.DocCurrency)).Sum(cn => cn.DocTotal),
                TotalAmountZIG = g.Where(cn => cn.DocCurrency == "ZIG" || cn.DocCurrency == "ZiG").Sum(cn => cn.DocTotal)
            })
            .OrderByDescending(c => c.TotalAmountUSD + c.TotalAmountZIG)
            .ToList();

        // Daily breakdown
        var dailyBreakdown = creditNotes
            .GroupBy(cn => ParseSapDate(cn.DocDate).Date)
            .Where(g => g.Key != DateTime.MinValue.Date)
            .Select(g => new DailyCreditNoteDto
            {
                Date = g.Key,
                Count = g.Count(),
                TotalAmountUSD = g.Where(cn => cn.DocCurrency == "USD" || cn.DocCurrency == "$" || string.IsNullOrEmpty(cn.DocCurrency)).Sum(cn => cn.DocTotal),
                TotalAmountZIG = g.Where(cn => cn.DocCurrency == "ZIG" || cn.DocCurrency == "ZiG").Sum(cn => cn.DocTotal)
            })
            .OrderBy(d => d.Date)
            .ToList();

        // Top products returned
        var topProducts = creditNotes
            .Where(cn => cn.DocumentLines != null)
            .SelectMany(cn => cn.DocumentLines!.Select(l => new { Line = l, cn.DocCurrency }))
            .GroupBy(x => new { x.Line.ItemCode, x.Line.ItemDescription })
            .Select(g => new CreditNoteByProductDto
            {
                ItemCode = g.Key.ItemCode ?? "Unknown",
                ItemName = g.Key.ItemDescription ?? g.Key.ItemCode ?? "Unknown",
                TotalQuantityReturned = g.Sum(x => Math.Abs(x.Line.Quantity)),
                TotalCreditAmountUSD = g.Where(x => x.DocCurrency == "USD" || x.DocCurrency == "$" || string.IsNullOrEmpty(x.DocCurrency)).Sum(x => Math.Abs(x.Line.LineTotal)),
                TotalCreditAmountZIG = g.Where(x => x.DocCurrency == "ZIG" || x.DocCurrency == "ZiG").Sum(x => Math.Abs(x.Line.LineTotal)),
                TimesReturned = g.Count()
            })
            .OrderByDescending(p => p.TotalQuantityReturned)
            .Take(20)
            .ToList();

        // Calculate credit-to-sales ratio
        decimal creditToSalesRatio = 0;
        try
        {
            var invoices = await _sapClient.GetInvoicesByDateRangeAsync(fromDate, toDate, cancellationToken);
            var totalSalesUSD = invoices.Where(i => i.DocCurrency == "USD" || i.DocCurrency == "$" || string.IsNullOrEmpty(i.DocCurrency)).Sum(i => i.DocTotal);
            if (totalSalesUSD > 0)
                creditToSalesRatio = Math.Round(usdCNs.Sum(cn => cn.DocTotal) / totalSalesUSD * 100, 2);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not calculate credit-to-sales ratio");
        }

        return new CreditNoteSummaryReportDto
        {
            FromDate = fromDate,
            ToDate = toDate,
            TotalCreditNotes = creditNotes.Count,
            TotalCreditAmountUSD = usdCNs.Sum(cn => cn.DocTotal),
            TotalCreditAmountZIG = zigCNs.Sum(cn => cn.DocTotal),
            TotalVatUSD = usdCNs.Sum(cn => cn.VatSum),
            TotalVatZIG = zigCNs.Sum(cn => cn.VatSum),
            AverageCreditNoteValueUSD = usdCNs.Count > 0 ? Math.Round(usdCNs.Average(cn => cn.DocTotal), 2) : 0,
            UniqueCustomers = creditNotes.Select(cn => cn.CardCode).Distinct().Count(),
            CreditToSalesRatioPercent = creditToSalesRatio,
            ByCustomer = byCustomer,
            DailyBreakdown = dailyBreakdown,
            TopProductsReturned = topProducts
        };
    }

    /// <summary>
    /// Get purchase order summary from SAP
    /// </summary>
    public async Task<PurchaseOrderSummaryReportDto> GetPurchaseOrderSummaryAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating purchase order summary from SAP: {FromDate} to {ToDate}", fromDate, toDate);

        var purchaseOrders = await _sapClient.GetPurchaseOrdersByDateRangeAsync(fromDate, toDate, cancellationToken);

        var openPOs = purchaseOrders.Where(po => po.DocumentStatus == "bost_Open" && po.Cancelled != "tYES").ToList();
        var closedPOs = purchaseOrders.Where(po => po.DocumentStatus == "bost_Close" && po.Cancelled != "tYES").ToList();
        var cancelledPOs = purchaseOrders.Where(po => po.Cancelled == "tYES").ToList();
        var activePOs = purchaseOrders.Where(po => po.Cancelled != "tYES").ToList();

        var usdPOs = activePOs.Where(po => po.DocCurrency == "USD" || po.DocCurrency == "$" || string.IsNullOrEmpty(po.DocCurrency)).ToList();
        var zigPOs = activePOs.Where(po => po.DocCurrency == "ZIG" || po.DocCurrency == "ZiG").ToList();

        // By supplier
        var bySupplier = activePOs
            .GroupBy(po => new { po.CardCode, po.CardName })
            .Select(g => new PurchaseOrderBySupplierDto
            {
                CardCode = g.Key.CardCode ?? "Unknown",
                CardName = g.Key.CardName ?? g.Key.CardCode ?? "Unknown",
                OrderCount = g.Count(),
                TotalValueUSD = g.Where(po => po.DocCurrency == "USD" || po.DocCurrency == "$" || string.IsNullOrEmpty(po.DocCurrency)).Sum(po => po.DocTotal ?? 0),
                TotalValueZIG = g.Where(po => po.DocCurrency == "ZIG" || po.DocCurrency == "ZiG").Sum(po => po.DocTotal ?? 0),
                OpenOrders = g.Count(po => po.DocumentStatus == "bost_Open"),
                PendingValueUSD = g.Where(po => po.DocumentStatus == "bost_Open" && (po.DocCurrency == "USD" || po.DocCurrency == "$" || string.IsNullOrEmpty(po.DocCurrency))).Sum(po => po.DocTotal ?? 0)
            })
            .OrderByDescending(s => s.TotalValueUSD + s.TotalValueZIG)
            .ToList();

        // Daily breakdown
        var dailyBreakdown = activePOs
            .GroupBy(po => ParseSapDate(po.DocDate).Date)
            .Where(g => g.Key != DateTime.MinValue.Date)
            .Select(g => new DailyPurchaseOrderDto
            {
                Date = g.Key,
                Count = g.Count(),
                TotalValueUSD = g.Where(po => po.DocCurrency == "USD" || po.DocCurrency == "$" || string.IsNullOrEmpty(po.DocCurrency)).Sum(po => po.DocTotal ?? 0),
                TotalValueZIG = g.Where(po => po.DocCurrency == "ZIG" || po.DocCurrency == "ZiG").Sum(po => po.DocTotal ?? 0)
            })
            .OrderBy(d => d.Date)
            .ToList();

        // Top purchased products
        var topProducts = activePOs
            .Where(po => po.DocumentLines != null)
            .SelectMany(po => po.DocumentLines!.Select(l => new { Line = l, po.DocCurrency }))
            .GroupBy(x => new { x.Line.ItemCode, x.Line.ItemDescription })
            .Select((g, index) => new TopPurchasedProductDto
            {
                Rank = index + 1,
                ItemCode = g.Key.ItemCode ?? "Unknown",
                ItemName = g.Key.ItemDescription ?? g.Key.ItemCode ?? "Unknown",
                TotalQuantityOrdered = g.Sum(x => x.Line.Quantity ?? 0),
                TotalCostUSD = g.Where(x => x.DocCurrency == "USD" || x.DocCurrency == "$" || string.IsNullOrEmpty(x.DocCurrency)).Sum(x => x.Line.LineTotal ?? 0),
                TotalCostZIG = g.Where(x => x.DocCurrency == "ZIG" || x.DocCurrency == "ZiG").Sum(x => x.Line.LineTotal ?? 0),
                TimesOrdered = g.Count()
            })
            .OrderByDescending(p => p.TotalCostUSD + p.TotalCostZIG)
            .Take(20)
            .ToList();

        // Re-rank after ordering
        for (int i = 0; i < topProducts.Count; i++) topProducts[i].Rank = i + 1;

        return new PurchaseOrderSummaryReportDto
        {
            FromDate = fromDate,
            ToDate = toDate,
            TotalPurchaseOrders = activePOs.Count,
            OpenOrders = openPOs.Count,
            ClosedOrders = closedPOs.Count,
            CancelledOrders = cancelledPOs.Count,
            TotalOrderValueUSD = usdPOs.Sum(po => po.DocTotal ?? 0),
            TotalOrderValueZIG = zigPOs.Sum(po => po.DocTotal ?? 0),
            TotalPendingValueUSD = openPOs.Where(po => po.DocCurrency == "USD" || po.DocCurrency == "$" || string.IsNullOrEmpty(po.DocCurrency)).Sum(po => po.DocTotal ?? 0),
            TotalPendingValueZIG = openPOs.Where(po => po.DocCurrency == "ZIG" || po.DocCurrency == "ZiG").Sum(po => po.DocTotal ?? 0),
            AverageOrderValueUSD = usdPOs.Count > 0 ? Math.Round(usdPOs.Average(po => po.DocTotal ?? 0), 2) : 0,
            UniqueSuppliers = activePOs.Select(po => po.CardCode).Distinct().Count(),
            BySupplier = bySupplier,
            DailyBreakdown = dailyBreakdown,
            TopProducts = topProducts
        };
    }

    /// <summary>
    /// Get receivables aging report from SAP invoices
    /// </summary>
    public async Task<ReceivablesAgingReportDto> GetReceivablesAgingAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating receivables aging report from SAP");

        // Get all open invoices (last 365 days to catch aged items)
        var fromDate = DateTime.UtcNow.AddDays(-365);
        var toDate = DateTime.UtcNow;
        var invoices = await _sapClient.GetInvoicesByDateRangeAsync(fromDate, toDate, cancellationToken);

        // Filter to only unpaid/partially paid invoices
        var openInvoices = invoices.Where(i => i.DocTotal - i.PaidToDate > 0.01m).ToList();

        var today = DateTime.UtcNow.Date;

        var agingItems = openInvoices.Select(inv =>
        {
            var docDate = ParseSapDate(inv.DocDate);
            var outstanding = inv.DocTotal - inv.PaidToDate;
            var daysOutstanding = docDate != DateTime.MinValue ? (int)(today - docDate.Date).TotalDays : 999;
            var isUSD = inv.DocCurrency == "USD" || inv.DocCurrency == "$" || string.IsNullOrEmpty(inv.DocCurrency);
            var isZIG = inv.DocCurrency == "ZIG" || inv.DocCurrency == "ZiG";

            return new
            {
                inv.CardCode,
                inv.CardName,
                Outstanding = outstanding,
                OutstandingUSD = isUSD ? outstanding : 0,
                OutstandingZIG = isZIG ? outstanding : 0,
                DaysOutstanding = daysOutstanding,
                Bucket = daysOutstanding <= 30 ? "Current" : daysOutstanding <= 60 ? "31-60" : daysOutstanding <= 90 ? "61-90" : "90+"
            };
        }).ToList();

        var totalUSD = agingItems.Sum(a => a.OutstandingUSD);
        var totalZIG = agingItems.Sum(a => a.OutstandingZIG);
        var totalAll = totalUSD + totalZIG;

        var currentItems = agingItems.Where(a => a.Bucket == "Current").ToList();
        var days31Items = agingItems.Where(a => a.Bucket == "31-60").ToList();
        var days61Items = agingItems.Where(a => a.Bucket == "61-90").ToList();
        var over90Items = agingItems.Where(a => a.Bucket == "90+").ToList();

        AgingBucketDto MakeBucket(string label, List<dynamic> items, decimal usdTotal, decimal zigTotal)
        {
            var usd = items.Sum(a => (decimal)a.OutstandingUSD);
            var zig = items.Sum(a => (decimal)a.OutstandingZIG);
            return new AgingBucketDto
            {
                Label = label,
                InvoiceCount = items.Count,
                AmountUSD = usd,
                AmountZIG = zig,
                PercentOfTotal = totalAll > 0 ? Math.Round((usd + zig) / totalAll * 100, 1) : 0
            };
        }

        // Customer aging
        var customerAging = agingItems
            .GroupBy(a => new { a.CardCode, a.CardName })
            .Select(g => new CustomerAgingDto
            {
                CardCode = (string)(g.Key.CardCode ?? "Unknown"),
                CardName = (string)(g.Key.CardName ?? g.Key.CardCode ?? "Unknown"),
                CurrentUSD = g.Where(a => a.Bucket == "Current").Sum(a => a.OutstandingUSD),
                Days31To60USD = g.Where(a => a.Bucket == "31-60").Sum(a => a.OutstandingUSD),
                Days61To90USD = g.Where(a => a.Bucket == "61-90").Sum(a => a.OutstandingUSD),
                Over90DaysUSD = g.Where(a => a.Bucket == "90+").Sum(a => a.OutstandingUSD),
                TotalOutstandingUSD = g.Sum(a => a.OutstandingUSD),
                TotalOutstandingZIG = g.Sum(a => a.OutstandingZIG),
                TotalInvoices = g.Count()
            })
            .OrderByDescending(c => c.TotalOutstandingUSD + c.TotalOutstandingZIG)
            .ToList();

        return new ReceivablesAgingReportDto
        {
            ReportDate = DateTime.UtcNow,
            TotalCustomers = customerAging.Count,
            TotalOutstandingUSD = totalUSD,
            TotalOutstandingZIG = totalZIG,
            Current = MakeBucket("0-30 Days", currentItems.Cast<dynamic>().ToList(), totalUSD, totalZIG),
            Days31To60 = MakeBucket("31-60 Days", days31Items.Cast<dynamic>().ToList(), totalUSD, totalZIG),
            Days61To90 = MakeBucket("61-90 Days", days61Items.Cast<dynamic>().ToList(), totalUSD, totalZIG),
            Over90Days = MakeBucket("90+ Days", over90Items.Cast<dynamic>().ToList(), totalUSD, totalZIG),
            CustomerAging = customerAging
        };
    }

    /// <summary>
    /// Get profit overview from SAP: revenue, credit notes, payments, purchase costs
    /// </summary>
    public async Task<ProfitOverviewReportDto> GetProfitOverviewAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating profit overview from SAP: {FromDate} to {ToDate}", fromDate, toDate);

        // Fetch data in parallel batches (SAP session is sequential, but we can interleave)
        var invoicesTask = _sapClient.GetInvoicesByDateRangeAsync(fromDate, toDate, cancellationToken);
        var invoices = await invoicesTask;

        var paymentsTask = _sapClient.GetIncomingPaymentsByDateRangeAsync(fromDate, toDate, cancellationToken);
        var payments = await paymentsTask;

        var creditNotesTask = _sapClient.GetCreditNotesByDateRangeAsync(fromDate, toDate, cancellationToken);
        var creditNotes = await creditNotesTask;
        creditNotes = creditNotes.Where(cn => cn.Cancelled != "tYES").ToList();

        List<SAPPurchaseOrder> purchaseOrders;
        try
        {
            purchaseOrders = await _sapClient.GetPurchaseOrdersByDateRangeAsync(fromDate, toDate, cancellationToken);
            purchaseOrders = purchaseOrders.Where(po => po.Cancelled != "tYES").ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch purchase orders for profit overview");
            purchaseOrders = new List<SAPPurchaseOrder>();
        }

        // Revenue
        var revenueUSD = invoices.Where(i => i.DocCurrency == "USD" || i.DocCurrency == "$" || string.IsNullOrEmpty(i.DocCurrency)).Sum(i => i.DocTotal);
        var revenueZIG = invoices.Where(i => i.DocCurrency == "ZIG" || i.DocCurrency == "ZiG").Sum(i => i.DocTotal);
        var vatUSD = invoices.Where(i => i.DocCurrency == "USD" || i.DocCurrency == "$" || string.IsNullOrEmpty(i.DocCurrency)).Sum(i => i.VatSum);
        var vatZIG = invoices.Where(i => i.DocCurrency == "ZIG" || i.DocCurrency == "ZiG").Sum(i => i.VatSum);

        // Credit notes
        var cnUSD = creditNotes.Where(cn => cn.DocCurrency == "USD" || cn.DocCurrency == "$" || string.IsNullOrEmpty(cn.DocCurrency)).Sum(cn => cn.DocTotal);
        var cnZIG = creditNotes.Where(cn => cn.DocCurrency == "ZIG" || cn.DocCurrency == "ZiG").Sum(cn => cn.DocTotal);

        // Payments collected
        static decimal GetPaymentTotal(IncomingPayment p) => p.CashSum + p.CheckSum + p.TransferSum + p.CreditSum;
        var collectedUSD = payments.Where(p => p.DocCurrency == "USD" || p.DocCurrency == "$" || string.IsNullOrEmpty(p.DocCurrency)).Sum(p => GetPaymentTotal(p));
        var collectedZIG = payments.Where(p => p.DocCurrency == "ZIG" || p.DocCurrency == "ZiG").Sum(p => GetPaymentTotal(p));

        // Purchase costs
        var purchaseCostUSD = purchaseOrders.Where(po => po.DocCurrency == "USD" || po.DocCurrency == "$" || string.IsNullOrEmpty(po.DocCurrency)).Sum(po => po.DocTotal ?? 0);
        var purchaseCostZIG = purchaseOrders.Where(po => po.DocCurrency == "ZIG" || po.DocCurrency == "ZiG").Sum(po => po.DocTotal ?? 0);

        var netRevenueUSD = revenueUSD - cnUSD;
        var netRevenueZIG = revenueZIG - cnZIG;
        var grossProfitUSD = netRevenueUSD - vatUSD - purchaseCostUSD;
        var grossProfitZIG = netRevenueZIG - vatZIG - purchaseCostZIG;
        var grossMargin = netRevenueUSD > 0 ? Math.Round(grossProfitUSD / netRevenueUSD * 100, 1) : 0;
        var collectionRate = revenueUSD > 0 ? Math.Round(collectedUSD / revenueUSD * 100, 1) : 0;

        // Monthly breakdown
        var months = Enumerable.Range(0, (int)Math.Ceiling((toDate - fromDate).TotalDays / 30.0) + 1)
            .Select(i => fromDate.AddMonths(i))
            .Select(d => new { Year = d.Year, Month = d.Month })
            .Distinct()
            .ToList();

        var monthlyBreakdown = months.Select(m =>
        {
            var mInvoices = invoices.Where(i => { var d = ParseSapDate(i.DocDate); return d.Year == m.Year && d.Month == m.Month; }).ToList();
            var mPayments = payments.Where(p => { var d = ParseSapDate(p.DocDate); return d.Year == m.Year && d.Month == m.Month; }).ToList();
            var mCreditNotes = creditNotes.Where(cn => { var d = ParseSapDate(cn.DocDate); return d.Year == m.Year && d.Month == m.Month; }).ToList();
            var mPurchases = purchaseOrders.Where(po => { var d = ParseSapDate(po.DocDate); return d.Year == m.Year && d.Month == m.Month; }).ToList();

            var mRevenueUSD = mInvoices.Where(i => i.DocCurrency == "USD" || i.DocCurrency == "$" || string.IsNullOrEmpty(i.DocCurrency)).Sum(i => i.DocTotal);
            var mRevenueZIG = mInvoices.Where(i => i.DocCurrency == "ZIG" || i.DocCurrency == "ZiG").Sum(i => i.DocTotal);
            var mCnUSD = mCreditNotes.Where(cn => cn.DocCurrency == "USD" || cn.DocCurrency == "$" || string.IsNullOrEmpty(cn.DocCurrency)).Sum(cn => cn.DocTotal);
            var mCollectedUSD = mPayments.Where(p => p.DocCurrency == "USD" || p.DocCurrency == "$" || string.IsNullOrEmpty(p.DocCurrency)).Sum(p => GetPaymentTotal(p));
            var mPurchaseCostUSD = mPurchases.Where(po => po.DocCurrency == "USD" || po.DocCurrency == "$" || string.IsNullOrEmpty(po.DocCurrency)).Sum(po => po.DocTotal ?? 0);

            return new MonthlyProfitDto
            {
                Month = new DateTime(m.Year, m.Month, 1).ToString("MMM yyyy"),
                RevenueUSD = mRevenueUSD,
                RevenueZIG = mRevenueZIG,
                CreditNotesUSD = mCnUSD,
                CollectedUSD = mCollectedUSD,
                PurchaseCostUSD = mPurchaseCostUSD,
                GrossProfitUSD = mRevenueUSD - mCnUSD - mPurchaseCostUSD,
                InvoiceCount = mInvoices.Count,
                PaymentCount = mPayments.Count
            };
        }).ToList();

        return new ProfitOverviewReportDto
        {
            FromDate = fromDate,
            ToDate = toDate,
            TotalRevenueUSD = revenueUSD,
            TotalRevenueZIG = revenueZIG,
            TotalCreditNotesUSD = cnUSD,
            TotalCreditNotesZIG = cnZIG,
            NetRevenueUSD = netRevenueUSD,
            NetRevenueZIG = netRevenueZIG,
            TotalCollectedUSD = collectedUSD,
            TotalCollectedZIG = collectedZIG,
            CollectionRatePercent = collectionRate,
            OutstandingReceivablesUSD = netRevenueUSD - collectedUSD,
            OutstandingReceivablesZIG = netRevenueZIG - collectedZIG,
            TotalVatUSD = vatUSD,
            TotalVatZIG = vatZIG,
            TotalPurchaseCostUSD = purchaseCostUSD,
            TotalPurchaseCostZIG = purchaseCostZIG,
            GrossProfitUSD = grossProfitUSD,
            GrossProfitZIG = grossProfitZIG,
            GrossMarginPercent = grossMargin,
            TotalInvoices = invoices.Count,
            TotalCreditNoteCount = creditNotes.Count,
            TotalPayments = payments.Count,
            UniqueCustomers = invoices.Select(i => i.CardCode).Distinct().Count(),
            MonthlyBreakdown = monthlyBreakdown
        };
    }
}
