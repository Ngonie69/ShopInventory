using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

/// <summary>
/// Interface for report service
/// </summary>
public interface IReportService
{
    Task<SalesSummaryReport?> GetSalesSummaryAsync(DateTime? fromDate, DateTime? toDate);
    Task<TopProductsReport?> GetTopProductsAsync(DateTime? fromDate, DateTime? toDate, int topCount = 10);
    Task<StockSummaryReport?> GetStockSummaryAsync(string? warehouseCode = null);
    Task<LowStockAlertReport?> GetLowStockAlertsAsync(string? warehouseCode = null, decimal? threshold = null);
    Task<PaymentSummaryReport?> GetPaymentSummaryAsync(DateTime? fromDate, DateTime? toDate);
    Task<TopCustomersReport?> GetTopCustomersAsync(DateTime? fromDate, DateTime? toDate, int topCount = 10);
    Task<OrderFulfillmentReport?> GetOrderFulfillmentAsync(DateTime? fromDate, DateTime? toDate);
    Task<CreditNoteSummaryReport?> GetCreditNoteSummaryAsync(DateTime? fromDate, DateTime? toDate);
    Task<PurchaseOrderSummaryReport?> GetPurchaseOrderSummaryAsync(DateTime? fromDate, DateTime? toDate);
    Task<ReceivablesAgingReport?> GetReceivablesAgingAsync();
    Task<ProfitOverviewReport?> GetProfitOverviewAsync(DateTime? fromDate, DateTime? toDate);
    Task<SlowMovingProductsReport?> GetSlowMovingProductsAsync(DateTime? fromDate, DateTime? toDate, int daysThreshold = 30);
}

/// <summary>
/// Report service implementation
/// </summary>
public class ReportService : IReportService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ReportService> _logger;

    public ReportService(HttpClient httpClient, ILogger<ReportService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<SalesSummaryReport?> GetSalesSummaryAsync(DateTime? fromDate, DateTime? toDate)
    {
        var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        try
        {
            var response = await _httpClient.GetAsync($"api/report/sales-summary?fromDate={from}&toDate={to}");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<SalesSummaryReport>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching sales summary report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch sales summary (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<TopProductsReport?> GetTopProductsAsync(DateTime? fromDate, DateTime? toDate, int topCount = 10)
    {
        var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        try
        {
            var response = await _httpClient.GetAsync($"api/report/top-products?fromDate={from}&toDate={to}&topCount={topCount}");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<TopProductsReport>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching top products report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch top products (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<StockSummaryReport?> GetStockSummaryAsync(string? warehouseCode = null)
    {
        var url = "api/report/stock-summary";
        if (!string.IsNullOrEmpty(warehouseCode))
            url += $"?warehouseCode={warehouseCode}";
        try
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<StockSummaryReport>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching stock summary report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch stock summary (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<LowStockAlertReport?> GetLowStockAlertsAsync(string? warehouseCode = null, decimal? threshold = null)
    {
        var url = "api/report/low-stock-alerts";
        var parameters = new List<string>();
        if (!string.IsNullOrEmpty(warehouseCode))
            parameters.Add($"warehouseCode={warehouseCode}");
        if (threshold.HasValue)
            parameters.Add($"threshold={threshold}");
        if (parameters.Any())
            url += "?" + string.Join("&", parameters);
        try
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<LowStockAlertReport>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching low stock alerts (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch low stock alerts (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<PaymentSummaryReport?> GetPaymentSummaryAsync(DateTime? fromDate, DateTime? toDate)
    {
        var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        try
        {
            var response = await _httpClient.GetAsync($"api/report/payment-summary?fromDate={from}&toDate={to}");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<PaymentSummaryReport>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching payment summary report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch payment summary (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<TopCustomersReport?> GetTopCustomersAsync(DateTime? fromDate, DateTime? toDate, int topCount = 10)
    {
        var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        try
        {
            var response = await _httpClient.GetAsync($"api/report/top-customers?fromDate={from}&toDate={to}&topCount={topCount}");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<TopCustomersReport>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching top customers report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch top customers (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<OrderFulfillmentReport?> GetOrderFulfillmentAsync(DateTime? fromDate, DateTime? toDate)
    {
        var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        try
        {
            var response = await _httpClient.GetAsync($"api/report/order-fulfillment?fromDate={from}&toDate={to}");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<OrderFulfillmentReport>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching order fulfillment report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch order fulfillment (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<CreditNoteSummaryReport?> GetCreditNoteSummaryAsync(DateTime? fromDate, DateTime? toDate)
    {
        var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        try
        {
            var response = await _httpClient.GetAsync($"api/report/credit-notes?fromDate={from}&toDate={to}");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<CreditNoteSummaryReport>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching credit notes report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch credit notes report (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<PurchaseOrderSummaryReport?> GetPurchaseOrderSummaryAsync(DateTime? fromDate, DateTime? toDate)
    {
        var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        try
        {
            var response = await _httpClient.GetAsync($"api/report/purchase-orders?fromDate={from}&toDate={to}");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<PurchaseOrderSummaryReport>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching purchase orders report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch purchase orders report (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<ReceivablesAgingReport?> GetReceivablesAgingAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("api/report/receivables-aging");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ReceivablesAgingReport>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching receivables aging report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch receivables aging report (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<ProfitOverviewReport?> GetProfitOverviewAsync(DateTime? fromDate, DateTime? toDate)
    {
        var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        try
        {
            var response = await _httpClient.GetAsync($"api/report/profit-overview?fromDate={from}&toDate={to}");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ProfitOverviewReport>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching profit overview report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch profit overview report (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<SlowMovingProductsReport?> GetSlowMovingProductsAsync(DateTime? fromDate, DateTime? toDate, int daysThreshold = 30)
    {
        var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-90).ToString("yyyy-MM-dd");
        var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        try
        {
            var response = await _httpClient.GetAsync($"api/report/slow-moving-products?fromDate={from}&toDate={to}&daysThreshold={daysThreshold}");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<SlowMovingProductsReport>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching slow moving products report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch slow moving products report (HTTP {ex.StatusCode})", ex);
        }
    }
}
