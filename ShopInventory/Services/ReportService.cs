using Microsoft.Extensions.Caching.Memory;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using ShopInventory.DTOs;
using ShopInventory.Models;

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
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<ReportService> _logger;
    private static readonly TimeSpan ReportDataCacheDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ReportResultCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CacheTelemetryCounters> CacheTelemetry = new(StringComparer.Ordinal);

    private sealed class CacheTelemetryCounters
    {
        public long Hits;
        public long Misses;
    }

    public ReportService(ISAPServiceLayerClient sapClient, IMemoryCache memoryCache, ILogger<ReportService> logger)
    {
        _sapClient = sapClient;
        _memoryCache = memoryCache;
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

    private static bool IsUsdCurrency(string? currency) =>
        currency is "USD" or "$" || string.IsNullOrEmpty(currency);

    private static bool IsZigCurrency(string? currency) =>
        currency is "ZIG" or "ZiG";

    private static string BuildReportCacheKey(string prefix, params object?[] parts)
    {
        var normalizedParts = parts.Select(part => part switch
        {
            DateTime dateTime => (dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime.ToUniversalTime())
                .ToString("O"),
            null => "<null>",
            _ => part.ToString() ?? string.Empty
        });

        return $"{prefix}:{string.Join("|", normalizedParts)}";
    }

    private static string BuildReportQueryCode(string prefix) =>
        $"{prefix}_{Random.Shared.Next(100000, 999999)}";

    private static string ToSapSqlDate(DateTime date) =>
        date.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string SanitizeSqlValue(string value) =>
        value.Replace("'", "''");

    private static string BuildUsdCurrencyPredicate(string columnName) =>
        $"({columnName} = 'USD' OR {columnName} = '$' OR {columnName} IS NULL OR {columnName} = '')";

    private static string BuildZigCurrencyPredicate(string columnName) =>
        $"({columnName} = 'ZIG' OR {columnName} = 'ZiG')";

    private static object? GetRowValue(Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) ? value : null;

    private static string GetRowString(Dictionary<string, object?> row, string key) =>
        GetRowValue(row, key)?.ToString() ?? string.Empty;

    private static string GetRowStringOrDefault(Dictionary<string, object?> row, string key, string fallback = "Unknown")
    {
        var value = GetRowString(row, key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int GetRowInt(Dictionary<string, object?> row, string key)
    {
        var value = GetRowValue(row, key);
        if (value is null)
            return 0;

        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static decimal GetRowDecimal(Dictionary<string, object?> row, string key)
    {
        var value = GetRowValue(row, key);
        if (value is null)
            return 0;

        return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    private static DateTime GetRowDate(Dictionary<string, object?> row, string key) =>
        ParseSapDate(GetRowValue(row, key)?.ToString());

    private Task<List<Dictionary<string, object?>>> ExecuteReportSqlAsync(
        string queryCodePrefix,
        string queryName,
        string sqlText,
        CancellationToken cancellationToken) =>
        _sapClient.ExecuteRawSqlQueryAsync(
            BuildReportQueryCode(queryCodePrefix),
            queryName,
            sqlText,
            cancellationToken);

    private static CacheTelemetryCounters GetTelemetryCounters(string cacheName)
    {
        return CacheTelemetry.GetOrAdd(cacheName, static _ => new CacheTelemetryCounters());
    }

    private static int? GetCachedItemCount(object? value)
    {
        return value is ICollection collection ? collection.Count : null;
    }

    private void LogCacheHit(string cacheName, string cacheKey)
    {
        var counters = GetTelemetryCounters(cacheName);
        var hits = Interlocked.Increment(ref counters.Hits);
        var misses = Volatile.Read(ref counters.Misses);
        var totalRequests = hits + misses;
        var hitRate = totalRequests > 0 ? (double)hits / totalRequests : 1d;

        _logger.LogInformation(
            "Report cache hit for {CacheName}. CacheKey={CacheKey}, HitRate={HitRate:P1}, SapCallsAvoidedTotal={SapCallsAvoidedTotal}, SapCallsMadeTotal={SapCallsMadeTotal}",
            cacheName,
            cacheKey,
            hitRate,
            hits,
            misses);
    }

    private void LogCacheMiss(string cacheName, string cacheKey, TimeSpan loadDuration, object? value)
    {
        var counters = GetTelemetryCounters(cacheName);
        var misses = Interlocked.Increment(ref counters.Misses);
        var hits = Volatile.Read(ref counters.Hits);
        var totalRequests = hits + misses;
        var hitRate = totalRequests > 0 ? (double)hits / totalRequests : 0d;
        var itemCount = GetCachedItemCount(value);

        if (itemCount.HasValue)
        {
            _logger.LogInformation(
                "Report cache miss for {CacheName}. CacheKey={CacheKey}, LoadDurationMs={LoadDurationMs}, ItemCount={ItemCount}, HitRate={HitRate:P1}, SapCallsAvoidedTotal={SapCallsAvoidedTotal}, SapCallsMadeTotal={SapCallsMadeTotal}",
                cacheName,
                cacheKey,
                loadDuration.TotalMilliseconds,
                itemCount.Value,
                hitRate,
                hits,
                misses);

            return;
        }

        _logger.LogInformation(
            "Report cache miss for {CacheName}. CacheKey={CacheKey}, LoadDurationMs={LoadDurationMs}, HitRate={HitRate:P1}, SapCallsAvoidedTotal={SapCallsAvoidedTotal}, SapCallsMadeTotal={SapCallsMadeTotal}",
            cacheName,
            cacheKey,
            loadDuration.TotalMilliseconds,
            hitRate,
            hits,
            misses);
    }

    private bool TryGetCachedValue<T>(string cacheName, string cacheKey, out T? cachedValue)
        where T : class
    {
        if (_memoryCache.TryGetValue(cacheKey, out cachedValue) && cachedValue is not null)
        {
            LogCacheHit(cacheName, cacheKey);
            return true;
        }

        cachedValue = null;
        return false;
    }

    private void CacheValue<T>(string cacheName, string cacheKey, T value, TimeSpan duration, TimeSpan loadDuration)
        where T : class
    {
        _memoryCache.Set(cacheKey, value, duration);
        LogCacheMiss(cacheName, cacheKey, loadDuration, value);
    }

    private Task<T> GetOrCreateCachedAsync<T>(string cacheName, string cacheKey, TimeSpan duration, Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken)
        where T : class
    {
        if (TryGetCachedValue(cacheName, cacheKey, out T? cachedValue))
        {
            return Task.FromResult(cachedValue!);
        }

        return CreateAndCacheAsync(cacheName, cacheKey, duration, factory, cancellationToken);
    }

    private async Task<T> CreateAndCacheAsync<T>(string cacheName, string cacheKey, TimeSpan duration, Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken)
        where T : class
    {
        var stopwatch = Stopwatch.StartNew();
        var value = await factory(cancellationToken);
        stopwatch.Stop();
        CacheValue(cacheName, cacheKey, value, duration, stopwatch.Elapsed);
        return value;
    }

    private Task<List<Dictionary<string, object?>>> GetCachedReportSqlRowsAsync(
        string cacheName,
        string cacheKey,
        string queryCodePrefix,
        string queryName,
        string sqlText,
        CancellationToken cancellationToken)
    {
        return GetOrCreateCachedAsync(
            cacheName,
            cacheKey,
            ReportDataCacheDuration,
            token => ExecuteReportSqlAsync(queryCodePrefix, queryName, sqlText, token),
            cancellationToken);
    }

    private Task<List<Dictionary<string, object?>>> GetCachedSalesSummaryRowsAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken)
    {
        var fromStr = ToSapSqlDate(fromDate);
        var toStr = ToSapSqlDate(toDate);
        var currencyColumn = @"T0.""DocCur""";
        var usdPredicate = BuildUsdCurrencyPredicate(currencyColumn);
        var zigPredicate = BuildZigCurrencyPredicate(currencyColumn);

        var sqlText = $@"SELECT
            COUNT(T0.""DocEntry"") AS ""InvoiceCount"",
            SUM(CASE WHEN {usdPredicate} THEN 1 ELSE 0 END) AS ""InvoiceCountUSD"",
            SUM(CASE WHEN {zigPredicate} THEN 1 ELSE 0 END) AS ""InvoiceCountZIG"",
            SUM(CASE WHEN {usdPredicate} THEN T0.""DocTotal"" ELSE 0 END) AS ""TotalSalesUSD"",
            SUM(CASE WHEN {zigPredicate} THEN T0.""DocTotal"" ELSE 0 END) AS ""TotalSalesZIG"",
            SUM(CASE WHEN {usdPredicate} THEN T0.""VatSum"" ELSE 0 END) AS ""TotalVatUSD"",
            SUM(CASE WHEN {zigPredicate} THEN T0.""VatSum"" ELSE 0 END) AS ""TotalVatZIG"",
            COUNT(DISTINCT T0.""CardCode"") AS ""UniqueCustomers""
        FROM OINV T0
        WHERE T0.""DocDate"" BETWEEN '{fromStr}' AND '{toStr}'";

        return GetCachedReportSqlRowsAsync(
            "report-data:sales-summary",
            BuildReportCacheKey("report-data:sales-summary", fromDate, toDate),
            "RPT_SALES_SUM",
            "Report Sales Summary",
            sqlText,
            cancellationToken);
    }

    private Task<List<Dictionary<string, object?>>> GetCachedDailySalesRowsAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken)
    {
        var fromStr = ToSapSqlDate(fromDate);
        var toStr = ToSapSqlDate(toDate);
        var currencyColumn = @"T0.""DocCur""";
        var usdPredicate = BuildUsdCurrencyPredicate(currencyColumn);
        var zigPredicate = BuildZigCurrencyPredicate(currencyColumn);

        var sqlText = $@"SELECT
            T0.""DocDate"",
            COUNT(T0.""DocEntry"") AS ""InvoiceCount"",
            SUM(CASE WHEN {usdPredicate} THEN T0.""DocTotal"" ELSE 0 END) AS ""TotalSalesUSD"",
            SUM(CASE WHEN {zigPredicate} THEN T0.""DocTotal"" ELSE 0 END) AS ""TotalSalesZIG""
        FROM OINV T0
        WHERE T0.""DocDate"" BETWEEN '{fromStr}' AND '{toStr}'
        GROUP BY T0.""DocDate""
        ORDER BY T0.""DocDate""";

        return GetCachedReportSqlRowsAsync(
            "report-data:sales-daily",
            BuildReportCacheKey("report-data:sales-daily", fromDate, toDate),
            "RPT_SALES_DAY",
            "Report Daily Sales",
            sqlText,
            cancellationToken);
    }

    private Task<List<Dictionary<string, object?>>> GetCachedTopProductsRowsAsync(
        DateTime fromDate,
        DateTime toDate,
        int topCount,
        string? warehouseCode,
        CancellationToken cancellationToken)
    {
        var fromStr = ToSapSqlDate(fromDate);
        var toStr = ToSapSqlDate(toDate);
        var clampedTopCount = Math.Clamp(topCount, 1, 1000);
        var warehouseFilter = string.IsNullOrWhiteSpace(warehouseCode)
            ? string.Empty
            : $@" AND T1.""WhsCode"" = '{SanitizeSqlValue(warehouseCode)}'";
        var currencyColumn = @"T0.""DocCur""";
        var usdPredicate = BuildUsdCurrencyPredicate(currencyColumn);
        var zigPredicate = BuildZigCurrencyPredicate(currencyColumn);

        var sqlText = $@"SELECT TOP {clampedTopCount}
            T1.""ItemCode"",
            T2.""ItemName"",
            SUM(T1.""Quantity"") AS ""TotalQuantitySold"",
            SUM(CASE WHEN {usdPredicate} THEN T1.""LineTotal"" ELSE 0 END) AS ""TotalRevenueUSD"",
            SUM(CASE WHEN {zigPredicate} THEN T1.""LineTotal"" ELSE 0 END) AS ""TotalRevenueZIG"",
            COUNT(T1.""LineNum"") AS ""TimesOrdered""
        FROM OINV T0
        INNER JOIN INV1 T1 ON T0.""DocEntry"" = T1.""DocEntry""
        LEFT JOIN OITM T2 ON T1.""ItemCode"" = T2.""ItemCode""
        WHERE T0.""DocDate"" BETWEEN '{fromStr}' AND '{toStr}'{warehouseFilter}
        GROUP BY T1.""ItemCode"", T2.""ItemName""
        ORDER BY SUM(T1.""Quantity"") DESC";

        return GetCachedReportSqlRowsAsync(
            "report-data:top-products",
            BuildReportCacheKey("report-data:top-products", fromDate, toDate, clampedTopCount, warehouseCode),
            "RPT_TOP_PROD",
            "Report Top Products",
            sqlText,
            cancellationToken);
    }

    private Task<List<Dictionary<string, object?>>> GetCachedStockedItemRowsAsync(CancellationToken cancellationToken)
    {
        var sqlText = @"SELECT
            T0.""ItemCode"",
            T0.""ItemName"",
            SUM(T1.""OnHand"") AS ""CurrentStock""
        FROM OITM T0
        INNER JOIN OITW T1 ON T0.""ItemCode"" = T1.""ItemCode""
        WHERE T1.""OnHand"" > 0
        GROUP BY T0.""ItemCode"", T0.""ItemName""
        ORDER BY T0.""ItemCode""";

        return GetCachedReportSqlRowsAsync(
            "report-data:stocked-items",
            "report-data:stocked-items",
            "RPT_STK_ITEMS",
            "Report Stocked Items",
            sqlText,
            cancellationToken);
    }

    private Task<List<Dictionary<string, object?>>> GetCachedItemLastSaleRowsAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken)
    {
        var fromStr = ToSapSqlDate(fromDate);
        var toStr = ToSapSqlDate(toDate);

        var sqlText = $@"SELECT
            T1.""ItemCode"",
            MAX(T0.""DocDate"") AS ""LastSoldDate""
        FROM OINV T0
        INNER JOIN INV1 T1 ON T0.""DocEntry"" = T1.""DocEntry""
        WHERE T0.""DocDate"" BETWEEN '{fromStr}' AND '{toStr}'
        GROUP BY T1.""ItemCode""";

        return GetCachedReportSqlRowsAsync(
            "report-data:item-last-sales",
            BuildReportCacheKey("report-data:item-last-sales", fromDate, toDate),
            "RPT_LASTSALE",
            "Report Item Last Sales",
            sqlText,
            cancellationToken);
    }

    private Task<List<Dictionary<string, object?>>> GetCachedCustomerInvoiceRowsAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken)
    {
        var fromStr = ToSapSqlDate(fromDate);
        var toStr = ToSapSqlDate(toDate);
        var currencyColumn = @"T0.""DocCur""";
        var usdPredicate = BuildUsdCurrencyPredicate(currencyColumn);
        var zigPredicate = BuildZigCurrencyPredicate(currencyColumn);

        var sqlText = $@"SELECT
            T0.""CardCode"",
            T0.""CardName"",
            COUNT(T0.""DocEntry"") AS ""InvoiceCount"",
            SUM(CASE WHEN {usdPredicate} THEN T0.""DocTotal"" ELSE 0 END) AS ""TotalPurchasesUSD"",
            SUM(CASE WHEN {zigPredicate} THEN T0.""DocTotal"" ELSE 0 END) AS ""TotalPurchasesZIG""
        FROM OINV T0
        WHERE T0.""DocDate"" BETWEEN '{fromStr}' AND '{toStr}'
        GROUP BY T0.""CardCode"", T0.""CardName""";

        return GetCachedReportSqlRowsAsync(
            "report-data:customer-invoices",
            BuildReportCacheKey("report-data:customer-invoices", fromDate, toDate),
            "RPT_CUST_INV",
            "Report Customer Invoices",
            sqlText,
            cancellationToken);
    }

    private Task<List<Dictionary<string, object?>>> GetCachedReceivablesAgingRowsAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken)
    {
        var fromStr = ToSapSqlDate(fromDate);
        var toStr = ToSapSqlDate(toDate);
        var currentBoundary = ToSapSqlDate(DateTime.UtcNow.Date.AddDays(-30));
        var days31Boundary = ToSapSqlDate(DateTime.UtcNow.Date.AddDays(-60));
        var days61Boundary = ToSapSqlDate(DateTime.UtcNow.Date.AddDays(-90));
        var bucketSql = $@"CASE
            WHEN T0.""DocDate"" >= '{currentBoundary}' THEN 'Current'
            WHEN T0.""DocDate"" >= '{days31Boundary}' THEN '31-60'
            WHEN T0.""DocDate"" >= '{days61Boundary}' THEN '61-90'
            ELSE '90+'
        END";

        var sqlText = $@"SELECT
            T0.""CardCode"",
            T0.""CardName"",
            {bucketSql} AS ""AgingBucket"",
            T0.""DocCur"",
            COUNT(T0.""DocEntry"") AS ""InvoiceCount"",
            SUM(T0.""DocTotal"" - T0.""PaidToDate"") AS ""OutstandingAmount""
        FROM OINV T0
        WHERE T0.""DocDate"" BETWEEN '{fromStr}' AND '{toStr}'
            AND (T0.""DocTotal"" - T0.""PaidToDate"") > 0.01
        GROUP BY T0.""CardCode"", T0.""CardName"", {bucketSql}, T0.""DocCur""
        ORDER BY T0.""CardName""";

        return GetCachedReportSqlRowsAsync(
            "report-data:receivables-aging",
            BuildReportCacheKey("report-data:receivables-aging", fromDate, toDate, currentBoundary),
            "RPT_AR_AGING",
            "Report Receivables Aging",
            sqlText,
            cancellationToken);
    }

    private Task<List<WarehouseDto>> GetCachedWarehousesAsync(CancellationToken cancellationToken)
    {
        return GetOrCreateCachedAsync(
            "report-data:warehouses",
            "report-data:warehouses",
            ReportDataCacheDuration,
            token => _sapClient.GetWarehousesAsync(token),
            cancellationToken);
    }

    private Task<List<StockQuantityDto>> GetCachedStockQuantitiesInWarehouseAsync(string warehouseCode, CancellationToken cancellationToken)
    {
        return GetOrCreateCachedAsync(
            "report-data:stock",
            BuildReportCacheKey("report-data:stock", warehouseCode),
            ReportDataCacheDuration,
            token => _sapClient.GetStockQuantitiesInWarehouseAsync(warehouseCode, token),
            cancellationToken);
    }

    private Task<List<InventoryTransfer>> GetCachedInventoryTransfersByDateRangeAsync(string warehouseCode, DateTime fromDate, DateTime toDate, CancellationToken cancellationToken)
    {
        return GetOrCreateCachedAsync(
            "report-data:transfers",
            BuildReportCacheKey("report-data:transfers", warehouseCode, fromDate, toDate),
            ReportDataCacheDuration,
            token => _sapClient.GetInventoryTransfersByDateRangeAsync(warehouseCode, fromDate, toDate, token),
            cancellationToken);
    }

    private Task<List<IncomingPayment>> GetCachedIncomingPaymentsByDateRangeAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken)
    {
        return GetOrCreateCachedAsync(
            "report-data:payments",
            BuildReportCacheKey("report-data:payments", fromDate, toDate),
            ReportDataCacheDuration,
            token => _sapClient.GetIncomingPaymentsByDateRangeAsync(fromDate, toDate, token),
            cancellationToken);
    }

    private Task<List<SAPCreditNote>> GetCachedCreditNotesByDateRangeAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken)
    {
        return GetOrCreateCachedAsync(
            "report-data:credit-notes",
            BuildReportCacheKey("report-data:credit-notes", fromDate, toDate),
            ReportDataCacheDuration,
            token => _sapClient.GetCreditNotesByDateRangeAsync(fromDate, toDate, token),
            cancellationToken);
    }

    private Task<List<SAPPurchaseOrder>> GetCachedPurchaseOrdersByDateRangeAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken)
    {
        return GetOrCreateCachedAsync(
            "report-data:purchase-orders",
            BuildReportCacheKey("report-data:purchase-orders", fromDate, toDate),
            ReportDataCacheDuration,
            token => _sapClient.GetPurchaseOrdersByDateRangeAsync(fromDate, toDate, token),
            cancellationToken);
    }

    /// <summary>
    /// Get sales summary report from SAP invoices
    /// </summary>
    public async Task<SalesSummaryReportDto> GetSalesSummaryAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildReportCacheKey("report-result:sales-summary", fromDate, toDate);
        return await GetOrCreateCachedAsync(
            "report-result:sales-summary",
            cacheKey,
            ReportResultCacheDuration,
            async token =>
            {
                _logger.LogInformation("Generating sales summary from SAP aggregates: {FromDate} to {ToDate}", fromDate, toDate);

                var summaryRows = await GetCachedSalesSummaryRowsAsync(fromDate, toDate, token);
                var dailyRows = await GetCachedDailySalesRowsAsync(fromDate, toDate, token);
                var summary = summaryRows.FirstOrDefault() ?? new Dictionary<string, object?>();

                var totalUSD = GetRowDecimal(summary, "TotalSalesUSD");
                var totalZIG = GetRowDecimal(summary, "TotalSalesZIG");
                var vatUSD = GetRowDecimal(summary, "TotalVatUSD");
                var vatZIG = GetRowDecimal(summary, "TotalVatZIG");
                var countUSD = GetRowInt(summary, "InvoiceCountUSD");
                var countZIG = GetRowInt(summary, "InvoiceCountZIG");

                var dailySales = dailyRows
                    .Select(row => new DailySalesDto
                    {
                        Date = GetRowDate(row, "DocDate").Date,
                        InvoiceCount = GetRowInt(row, "InvoiceCount"),
                        TotalSalesUSD = GetRowDecimal(row, "TotalSalesUSD"),
                        TotalSalesZIG = GetRowDecimal(row, "TotalSalesZIG")
                    })
                    .Where(d => d.Date != DateTime.MinValue.Date)
                    .OrderBy(d => d.Date)
                    .ToList();

                return new SalesSummaryReportDto
                {
                    FromDate = fromDate,
                    ToDate = toDate,
                    TotalInvoices = GetRowInt(summary, "InvoiceCount"),
                    TotalSalesUSD = totalUSD,
                    TotalSalesZIG = totalZIG,
                    TotalVatUSD = vatUSD,
                    TotalVatZIG = vatZIG,
                    AverageInvoiceValueUSD = countUSD > 0 ? totalUSD / countUSD : 0,
                    AverageInvoiceValueZIG = countZIG > 0 ? totalZIG / countZIG : 0,
                    UniqueCustomers = GetRowInt(summary, "UniqueCustomers"),
                    DailySales = dailySales,
                    SalesByCurrency = new List<SalesByCurrencyDto>
                    {
                        new() { Currency = "USD", InvoiceCount = countUSD, TotalSales = totalUSD, TotalVat = vatUSD },
                        new() { Currency = "ZIG", InvoiceCount = countZIG, TotalSales = totalZIG, TotalVat = vatZIG }
                    }
                };
            },
            cancellationToken);
    }

    /// <summary>
    /// Get top selling products from SAP invoice lines
    /// </summary>
    public async Task<TopProductsReportDto> GetTopProductsAsync(DateTime fromDate, DateTime toDate, int topCount = 10, string? warehouseCode = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating top products from SAP aggregates: {FromDate} to {ToDate}, top {Count}", fromDate, toDate, topCount);

        var rows = await GetCachedTopProductsRowsAsync(fromDate, toDate, topCount, warehouseCode, cancellationToken);
        var topProducts = rows
            .Select((p, index) => new TopProductDto
            {
                Rank = index + 1,
                ItemCode = GetRowStringOrDefault(p, "ItemCode"),
                ItemName = GetRowStringOrDefault(p, "ItemName", GetRowStringOrDefault(p, "ItemCode")),
                TotalQuantitySold = GetRowDecimal(p, "TotalQuantitySold"),
                TotalRevenueUSD = GetRowDecimal(p, "TotalRevenueUSD"),
                TotalRevenueZIG = GetRowDecimal(p, "TotalRevenueZIG"),
                TimesOrdered = GetRowInt(p, "TimesOrdered")
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
        _logger.LogInformation("Generating slow moving products from SAP aggregates, threshold {Days} days", daysThreshold);

        var stockedItems = await GetCachedStockedItemRowsAsync(cancellationToken);
        var lastSaleRows = await GetCachedItemLastSaleRowsAsync(fromDate, toDate, cancellationToken);
        var lastSaleLookup = lastSaleRows
            .Select(row => new
            {
                ItemCode = GetRowString(row, "ItemCode"),
                LastSoldDate = GetRowDate(row, "LastSoldDate")
            })
            .Where(row => !string.IsNullOrWhiteSpace(row.ItemCode))
            .GroupBy(row => row.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Max(row => row.LastSoldDate),
                StringComparer.OrdinalIgnoreCase);

        var today = DateTime.UtcNow;
        var slowMoving = stockedItems
            .Select(item => new SlowMovingProductDto
            {
                ItemCode = GetRowStringOrDefault(item, "ItemCode"),
                ItemName = GetRowStringOrDefault(item, "ItemName", GetRowStringOrDefault(item, "ItemCode")),
                CurrentStock = GetRowDecimal(item, "CurrentStock"),
                LastSoldDate = lastSaleLookup.TryGetValue(GetRowString(item, "ItemCode"), out var lastSale) && lastSale != DateTime.MinValue ? lastSale : null,
                DaysSinceLastSale = lastSaleLookup.TryGetValue(GetRowString(item, "ItemCode"), out var ls) && ls != DateTime.MinValue
                    ? (int)(today - ls).TotalDays : 999,
                StockValue = 0
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

        var warehouses = await GetCachedWarehousesAsync(cancellationToken);
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
                var stockItems = await GetCachedStockQuantitiesInWarehouseAsync(
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
            transfers = await GetCachedInventoryTransfersByDateRangeAsync(warehouseCode, fromDate, toDate, cancellationToken);
        }
        else
        {
            // Get transfers for all active warehouses (use first warehouse approach or get all)
            // The SAP client requires a warehouse code, so we'll get all and filter client-side
            var warehouses = await GetCachedWarehousesAsync(cancellationToken);
            var activeWhs = warehouses.Where(w => w.IsActive).Select(w => w.WarehouseCode!).ToList();

            transfers = new List<InventoryTransfer>();
            var seen = new HashSet<int>();
            foreach (var wh in activeWhs.Take(10)) // Limit to avoid too many SAP calls
            {
                try
                {
                    var whTransfers = await GetCachedInventoryTransfersByDateRangeAsync(wh, fromDate, toDate, cancellationToken);
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

        var warehouses = await GetCachedWarehousesAsync(cancellationToken);
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
                var stockItems = await GetCachedStockQuantitiesInWarehouseAsync(
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

        var payments = await GetCachedIncomingPaymentsByDateRangeAsync(fromDate, toDate, cancellationToken);

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
        _logger.LogInformation("Generating top customers from SAP aggregates: {FromDate} to {ToDate}, top {Count}", fromDate, toDate, topCount);

        var customerInvoiceRows = await GetCachedCustomerInvoiceRowsAsync(fromDate, toDate, cancellationToken);
        var payments = await GetCachedIncomingPaymentsByDateRangeAsync(fromDate, toDate, cancellationToken);

        var customerInvoices = customerInvoiceRows
            .Select(row => new
            {
                CardCode = GetRowStringOrDefault(row, "CardCode"),
                CardName = GetRowStringOrDefault(row, "CardName", GetRowStringOrDefault(row, "CardCode")),
                InvoiceCount = GetRowInt(row, "InvoiceCount"),
                TotalPurchasesUSD = GetRowDecimal(row, "TotalPurchasesUSD"),
                TotalPurchasesZIG = GetRowDecimal(row, "TotalPurchasesZIG")
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
        var cacheKey = BuildReportCacheKey("report-result:order-fulfillment", fromDate, toDate);
        if (TryGetCachedValue("report-result:order-fulfillment", cacheKey, out OrderFulfillmentReportDto? cachedReport))
        {
            return cachedReport!;
        }

        var generationStopwatch = Stopwatch.StartNew();

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

        var report = new OrderFulfillmentReportDto
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

        generationStopwatch.Stop();
        CacheValue("report-result:order-fulfillment", cacheKey, report, ReportResultCacheDuration, generationStopwatch.Elapsed);

        return report;
    }

    /// <summary>
    /// Get credit notes summary from SAP
    /// </summary>
    public async Task<CreditNoteSummaryReportDto> GetCreditNoteSummaryAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating credit note summary from SAP: {FromDate} to {ToDate}", fromDate, toDate);

        var creditNotes = await GetCachedCreditNotesByDateRangeAsync(fromDate, toDate, cancellationToken);
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
            var salesSummary = await GetSalesSummaryAsync(fromDate, toDate, cancellationToken);
            var totalSalesUSD = salesSummary.TotalSalesUSD;
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

        var purchaseOrders = await GetCachedPurchaseOrdersByDateRangeAsync(fromDate, toDate, cancellationToken);

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
        _logger.LogInformation("Generating receivables aging report from SAP aggregates");

        var fromDate = DateTime.UtcNow.AddDays(-365);
        var toDate = DateTime.UtcNow;
        var rows = await GetCachedReceivablesAgingRowsAsync(fromDate, toDate, cancellationToken);
        var totalUSD = 0m;
        var totalZIG = 0m;
        var current = new AgingBucketDto { Label = "0-30 Days" };
        var days31 = new AgingBucketDto { Label = "31-60 Days" };
        var days61 = new AgingBucketDto { Label = "61-90 Days" };
        var over90 = new AgingBucketDto { Label = "90+ Days" };
        var customerAging = new Dictionary<string, CustomerAgingDto>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var currency = GetRowString(row, "DocCur");
            var isUSD = IsUsdCurrency(currency);
            var isZIG = IsZigCurrency(currency);
            var amount = GetRowDecimal(row, "OutstandingAmount");
            var count = GetRowInt(row, "InvoiceCount");
            var bucket = GetRowString(row, "AgingBucket");
            var cardCode = GetRowStringOrDefault(row, "CardCode");
            var cardName = GetRowStringOrDefault(row, "CardName", cardCode);

            var bucketDto = bucket switch
            {
                "31-60" => days31,
                "61-90" => days61,
                "90+" => over90,
                _ => current
            };

            bucketDto.InvoiceCount += count;
            if (isUSD)
            {
                bucketDto.AmountUSD += amount;
                totalUSD += amount;
            }
            else if (isZIG)
            {
                bucketDto.AmountZIG += amount;
                totalZIG += amount;
            }

            if (!customerAging.TryGetValue(cardCode, out var customer))
            {
                customer = new CustomerAgingDto
                {
                    CardCode = cardCode,
                    CardName = cardName
                };
                customerAging[cardCode] = customer;
            }

            if (isUSD)
            {
                if (bucket == "31-60") customer.Days31To60USD += amount;
                else if (bucket == "61-90") customer.Days61To90USD += amount;
                else if (bucket == "90+") customer.Over90DaysUSD += amount;
                else customer.CurrentUSD += amount;

                customer.TotalOutstandingUSD += amount;
            }
            else if (isZIG)
            {
                customer.TotalOutstandingZIG += amount;
            }

            customer.TotalInvoices += count;
        }

        var totalAll = totalUSD + totalZIG;
        foreach (var bucket in new[] { current, days31, days61, over90 })
        {
            bucket.PercentOfTotal = totalAll > 0 ? Math.Round((bucket.AmountUSD + bucket.AmountZIG) / totalAll * 100, 1) : 0;
        }

        var customerAgingList = customerAging.Values
            .OrderByDescending(c => c.TotalOutstandingUSD + c.TotalOutstandingZIG)
            .ToList();

        return new ReceivablesAgingReportDto
        {
            ReportDate = DateTime.UtcNow,
            TotalCustomers = customerAgingList.Count,
            TotalOutstandingUSD = totalUSD,
            TotalOutstandingZIG = totalZIG,
            Current = current,
            Days31To60 = days31,
            Days61To90 = days61,
            Over90Days = over90,
            CustomerAging = customerAgingList
        };
    }

    /// <summary>
    /// Get profit overview from SAP: revenue, credit notes, payments, purchase costs
    /// </summary>
    public async Task<ProfitOverviewReportDto> GetProfitOverviewAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating profit overview from SAP: {FromDate} to {ToDate}", fromDate, toDate);

        var salesSummary = await GetSalesSummaryAsync(fromDate, toDate, cancellationToken);

        var payments = await GetCachedIncomingPaymentsByDateRangeAsync(fromDate, toDate, cancellationToken);

        var creditNotes = await GetCachedCreditNotesByDateRangeAsync(fromDate, toDate, cancellationToken);
        creditNotes = creditNotes.Where(cn => cn.Cancelled != "tYES").ToList();

        List<SAPPurchaseOrder> purchaseOrders;
        try
        {
            purchaseOrders = await GetCachedPurchaseOrdersByDateRangeAsync(fromDate, toDate, cancellationToken);
            purchaseOrders = purchaseOrders.Where(po => po.Cancelled != "tYES").ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch purchase orders for profit overview");
            purchaseOrders = new List<SAPPurchaseOrder>();
        }

        // Revenue
        var revenueUSD = salesSummary.TotalSalesUSD;
        var revenueZIG = salesSummary.TotalSalesZIG;
        var vatUSD = salesSummary.TotalVatUSD;
        var vatZIG = salesSummary.TotalVatZIG;

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
            var mPayments = payments.Where(p => { var d = ParseSapDate(p.DocDate); return d.Year == m.Year && d.Month == m.Month; }).ToList();
            var mCreditNotes = creditNotes.Where(cn => { var d = ParseSapDate(cn.DocDate); return d.Year == m.Year && d.Month == m.Month; }).ToList();
            var mPurchases = purchaseOrders.Where(po => { var d = ParseSapDate(po.DocDate); return d.Year == m.Year && d.Month == m.Month; }).ToList();

            var mSales = salesSummary.DailySales.Where(d => d.Date.Year == m.Year && d.Date.Month == m.Month).ToList();
            var mRevenueUSD = mSales.Sum(d => d.TotalSalesUSD);
            var mRevenueZIG = mSales.Sum(d => d.TotalSalesZIG);
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
                InvoiceCount = mSales.Sum(d => d.InvoiceCount),
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
            TotalInvoices = salesSummary.TotalInvoices,
            TotalCreditNoteCount = creditNotes.Count,
            TotalPayments = payments.Count,
            UniqueCustomers = salesSummary.UniqueCustomers,
            MonthlyBreakdown = monthlyBreakdown
        };
    }
}
