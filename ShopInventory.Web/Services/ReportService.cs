using ShopInventory.Web.Models;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace ShopInventory.Web.Services;

/// <summary>
/// Interface for report service
/// </summary>
public interface IReportService
{
    Task<SalesSummaryReport?> GetSalesSummaryAsync(DateTime? fromDate, DateTime? toDate, CancellationToken cancellationToken = default);
    Task<TopProductsReport?> GetTopProductsAsync(DateTime? fromDate, DateTime? toDate, int topCount = 10, CancellationToken cancellationToken = default);
    Task<StockSummaryReport?> GetStockSummaryAsync(string? warehouseCode = null, CancellationToken cancellationToken = default);
    Task<LowStockAlertReport?> GetLowStockAlertsAsync(string? warehouseCode = null, decimal? threshold = null, CancellationToken cancellationToken = default);
    Task<PaymentSummaryReport?> GetPaymentSummaryAsync(DateTime? fromDate, DateTime? toDate, CancellationToken cancellationToken = default);
    Task<TopCustomersReport?> GetTopCustomersAsync(DateTime? fromDate, DateTime? toDate, int topCount = 10, CancellationToken cancellationToken = default);
    Task<OrderFulfillmentReport?> GetOrderFulfillmentAsync(DateTime? fromDate, DateTime? toDate, string? cardCode = null, CancellationToken cancellationToken = default);
    Task<CreditNoteSummaryReport?> GetCreditNoteSummaryAsync(DateTime? fromDate, DateTime? toDate, CancellationToken cancellationToken = default);
    Task<PurchaseOrderSummaryReport?> GetPurchaseOrderSummaryAsync(DateTime? fromDate, DateTime? toDate, CancellationToken cancellationToken = default);
    Task<ReceivablesAgingReport?> GetReceivablesAgingAsync(CancellationToken cancellationToken = default);
    Task<ProfitOverviewReport?> GetProfitOverviewAsync(DateTime? fromDate, DateTime? toDate, CancellationToken cancellationToken = default);
    Task<SlowMovingProductsReport?> GetSlowMovingProductsAsync(DateTime? fromDate, DateTime? toDate, int daysThreshold = 30, CancellationToken cancellationToken = default);
}

/// <summary>
/// Report service implementation
/// </summary>
public class ReportService : IReportService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ReportService> _logger;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public ReportService(HttpClient httpClient, ILogger<ReportService> logger, IMemoryCache cache)
    {
        _httpClient = httpClient;
        _logger = logger;
        _cache = cache;
    }

    public async Task<SalesSummaryReport?> GetSalesSummaryAsync(DateTime? fromDate, DateTime? toDate, CancellationToken cancellationToken = default)
    {
        var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        var cacheKey = $"report:sales:{from}:{to}";
        if (_cache.TryGetValue(cacheKey, out SalesSummaryReport? cached)) return cached;
        try
        {
            var response = await _httpClient.GetAsync($"api/report/sales-summary?fromDate={from}&toDate={to}", cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<SalesSummaryReport>(cancellationToken);
            _cache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching sales summary report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch sales summary (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<TopProductsReport?> GetTopProductsAsync(DateTime? fromDate, DateTime? toDate, int topCount = 10, CancellationToken cancellationToken = default)
    {
        var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        var cacheKey = $"report:topproducts:{from}:{to}:{topCount}";
        if (_cache.TryGetValue(cacheKey, out TopProductsReport? cached)) return cached;
        try
        {
            var response = await _httpClient.GetAsync($"api/report/top-products?fromDate={from}&toDate={to}&topCount={topCount}", cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<TopProductsReport>(cancellationToken);
            _cache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching top products report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch top products (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<StockSummaryReport?> GetStockSummaryAsync(string? warehouseCode = null, CancellationToken cancellationToken = default)
    {
        var url = "api/report/stock-summary";
        if (!string.IsNullOrEmpty(warehouseCode))
            url += $"?warehouseCode={warehouseCode}";
        var cacheKey = $"report:stock:{warehouseCode}";
        if (_cache.TryGetValue(cacheKey, out StockSummaryReport? cached)) return cached;
        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<StockSummaryReport>(cancellationToken);
            _cache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching stock summary report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch stock summary (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<LowStockAlertReport?> GetLowStockAlertsAsync(string? warehouseCode = null, decimal? threshold = null, CancellationToken cancellationToken = default)
    {
        var url = "api/report/low-stock-alerts";
        var parameters = new List<string>();
        if (!string.IsNullOrEmpty(warehouseCode))
            parameters.Add($"warehouseCode={warehouseCode}");
        if (threshold.HasValue)
            parameters.Add($"threshold={threshold}");
        if (parameters.Any())
            url += "?" + string.Join("&", parameters);
        var cacheKey = $"report:lowstock:{warehouseCode}:{threshold}";
        if (_cache.TryGetValue(cacheKey, out LowStockAlertReport? cached)) return cached;
        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<LowStockAlertReport>(cancellationToken);
            _cache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching low stock alerts (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch low stock alerts (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<PaymentSummaryReport?> GetPaymentSummaryAsync(DateTime? fromDate, DateTime? toDate, CancellationToken cancellationToken = default)
    {
        var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        var cacheKey = $"report:payments:{from}:{to}";
        if (_cache.TryGetValue(cacheKey, out PaymentSummaryReport? cached)) return cached;
        try
        {
            var response = await _httpClient.GetAsync($"api/report/payment-summary?fromDate={from}&toDate={to}", cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<PaymentSummaryReport>(cancellationToken);
            _cache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching payment summary report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch payment summary (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<TopCustomersReport?> GetTopCustomersAsync(DateTime? fromDate, DateTime? toDate, int topCount = 10, CancellationToken cancellationToken = default)
    {
        var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        var cacheKey = $"report:topcustomers:{from}:{to}:{topCount}";
        if (_cache.TryGetValue(cacheKey, out TopCustomersReport? cached)) return cached;
        try
        {
            var response = await _httpClient.GetAsync($"api/report/top-customers?fromDate={from}&toDate={to}&topCount={topCount}", cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<TopCustomersReport>(cancellationToken);
            _cache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching top customers report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch top customers (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<OrderFulfillmentReport?> GetOrderFulfillmentAsync(DateTime? fromDate, DateTime? toDate, string? cardCode = null, CancellationToken cancellationToken = default)
    {
        var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        var customer = string.IsNullOrWhiteSpace(cardCode) ? null : cardCode.Trim();
        // The customer belongs in the key: without it a rep's single-partner report and the
        // company-wide one would answer each other for ten minutes.
        var cacheKey = $"report:fulfillment:{from}:{to}:{customer ?? "*"}";
        if (_cache.TryGetValue(cacheKey, out OrderFulfillmentReport? cached)) return cached;
        try
        {
            var url = $"api/report/order-fulfillment?fromDate={from}&toDate={to}";
            if (customer is not null)
                url += $"&cardCode={Uri.EscapeDataString(customer)}";

            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var reason = await ReadProblemDetailAsync(response, cancellationToken);
                _logger.LogError("Error fetching order fulfillment report (HTTP {StatusCode}): {Reason}", response.StatusCode, reason);
                throw new InvalidOperationException($"Failed to fetch order fulfillment: {reason}");
            }

            var result = await response.Content.ReadFromJsonAsync<OrderFulfillmentReport>(cancellationToken);
            _cache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching order fulfillment report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch order fulfillment (HTTP {ex.StatusCode})", ex);
        }
    }

    /// <summary>
    /// Reads the message the API actually sent with a failed report response.
    /// </summary>
    /// <remarks>
    /// Every report failure — a statement SAP rejected, a timed-out generation, a missing SAP
    /// session — reaches the controller as an <c>ErrorType.Failure</c>, which
    /// <c>ApiControllerBase.Problem</c> answers as 400 with the real reason in the ProblemDetails
    /// title. Calling <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/> discards that body,
    /// so the page could only ever say "(HTTP BadRequest)" — a sentence that is true of every one
    /// of those causes and identifies none of them. That is what made a SAP validator rejecting
    /// COALESCE in the order-line statement look like a page that was simply broken.
    /// </remarks>
    private static async Task<string> ReadProblemDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var fallback = $"HTTP {response.StatusCode}";

        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
                return fallback;

            using var document = JsonDocument.Parse(body);
            var title = ReadStringProperty(document.RootElement, "title");
            var detail = ReadStringProperty(document.RootElement, "detail");

            // The title carries the error; the detail is boilerplate on anything the API classifies
            // as a server fault, where it is the only thing that is not "An unexpected error
            // occurred." Prefer whichever is present, and keep the status for correlation.
            var message = title ?? detail;
            return string.IsNullOrWhiteSpace(message) ? fallback : $"{message} ({fallback})";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            // A non-ProblemDetails body is still a failure; report the status rather than masking it.
            return fallback;
        }
    }

    private static string? ReadStringProperty(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    public async Task<CreditNoteSummaryReport?> GetCreditNoteSummaryAsync(DateTime? fromDate, DateTime? toDate, CancellationToken cancellationToken = default)
    {
        var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        var cacheKey = $"report:creditnotes:{from}:{to}";
        if (_cache.TryGetValue(cacheKey, out CreditNoteSummaryReport? cached)) return cached;
        try
        {
            var response = await _httpClient.GetAsync($"api/report/credit-notes?fromDate={from}&toDate={to}", cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<CreditNoteSummaryReport>(cancellationToken);
            _cache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching credit notes report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch credit notes report (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<PurchaseOrderSummaryReport?> GetPurchaseOrderSummaryAsync(DateTime? fromDate, DateTime? toDate, CancellationToken cancellationToken = default)
    {
        var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        var cacheKey = $"report:purchasing:{from}:{to}";
        if (_cache.TryGetValue(cacheKey, out PurchaseOrderSummaryReport? cached)) return cached;
        try
        {
            var response = await _httpClient.GetAsync($"api/report/purchase-orders?fromDate={from}&toDate={to}", cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<PurchaseOrderSummaryReport>(cancellationToken);
            _cache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching purchase orders report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch purchase orders report (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<ReceivablesAgingReport?> GetReceivablesAgingAsync(CancellationToken cancellationToken = default)
    {
        var cacheKey = "report:aging";
        if (_cache.TryGetValue(cacheKey, out ReceivablesAgingReport? cached)) return cached;
        try
        {
            var response = await _httpClient.GetAsync("api/report/receivables-aging", cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<ReceivablesAgingReport>(cancellationToken);
            _cache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching receivables aging report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch receivables aging report (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<ProfitOverviewReport?> GetProfitOverviewAsync(DateTime? fromDate, DateTime? toDate, CancellationToken cancellationToken = default)
    {
        var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        var cacheKey = $"report:profit:{from}:{to}";
        if (_cache.TryGetValue(cacheKey, out ProfitOverviewReport? cached)) return cached;
        try
        {
            var response = await _httpClient.GetAsync($"api/report/profit-overview?fromDate={from}&toDate={to}", cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<ProfitOverviewReport>(cancellationToken);
            _cache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching profit overview report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch profit overview report (HTTP {ex.StatusCode})", ex);
        }
    }

    public async Task<SlowMovingProductsReport?> GetSlowMovingProductsAsync(DateTime? fromDate, DateTime? toDate, int daysThreshold = 30, CancellationToken cancellationToken = default)
    {
        var from = fromDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.AddDays(-90).ToString("yyyy-MM-dd");
        var to = toDate?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        var cacheKey = $"report:slowmoving:{from}:{to}:{daysThreshold}";
        if (_cache.TryGetValue(cacheKey, out SlowMovingProductsReport? cached)) return cached;
        try
        {
            var response = await _httpClient.GetAsync($"api/report/slow-moving-products?fromDate={from}&toDate={to}&daysThreshold={daysThreshold}", cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<SlowMovingProductsReport>(cancellationToken);
            _cache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching slow moving products report (HTTP {StatusCode})", ex.StatusCode);
            throw new InvalidOperationException($"Failed to fetch slow moving products report (HTTP {ex.StatusCode})", ex);
        }
    }
}
