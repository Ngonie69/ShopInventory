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
        try
        {
            var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
            var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
            return await _httpClient.GetFromJsonAsync<SalesSummaryReport>($"api/report/sales-summary?fromDate={from}&toDate={to}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching sales summary report");
            return null;
        }
    }

    public async Task<TopProductsReport?> GetTopProductsAsync(DateTime? fromDate, DateTime? toDate, int topCount = 10)
    {
        try
        {
            var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
            var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
            return await _httpClient.GetFromJsonAsync<TopProductsReport>($"api/report/top-products?fromDate={from}&toDate={to}&topCount={topCount}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching top products report");
            return null;
        }
    }

    public async Task<StockSummaryReport?> GetStockSummaryAsync(string? warehouseCode = null)
    {
        try
        {
            var url = "api/report/stock-summary";
            if (!string.IsNullOrEmpty(warehouseCode))
                url += $"?warehouseCode={warehouseCode}";
            return await _httpClient.GetFromJsonAsync<StockSummaryReport>(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching stock summary report");
            return null;
        }
    }

    public async Task<LowStockAlertReport?> GetLowStockAlertsAsync(string? warehouseCode = null, decimal? threshold = null)
    {
        try
        {
            var url = "api/report/low-stock-alerts";
            var parameters = new List<string>();
            if (!string.IsNullOrEmpty(warehouseCode))
                parameters.Add($"warehouseCode={warehouseCode}");
            if (threshold.HasValue)
                parameters.Add($"threshold={threshold}");
            if (parameters.Any())
                url += "?" + string.Join("&", parameters);
            return await _httpClient.GetFromJsonAsync<LowStockAlertReport>(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching low stock alerts");
            return null;
        }
    }

    public async Task<PaymentSummaryReport?> GetPaymentSummaryAsync(DateTime? fromDate, DateTime? toDate)
    {
        try
        {
            var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
            var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
            return await _httpClient.GetFromJsonAsync<PaymentSummaryReport>($"api/report/payment-summary?fromDate={from}&toDate={to}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching payment summary report");
            return null;
        }
    }

    public async Task<TopCustomersReport?> GetTopCustomersAsync(DateTime? fromDate, DateTime? toDate, int topCount = 10)
    {
        try
        {
            var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
            var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
            return await _httpClient.GetFromJsonAsync<TopCustomersReport>($"api/report/top-customers?fromDate={from}&toDate={to}&topCount={topCount}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching top customers report");
            return null;
        }
    }
}
