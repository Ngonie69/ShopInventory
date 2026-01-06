using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface IPaymentService
{
    Task<IncomingPaymentListResponse?> GetPaymentsAsync(int page = 1, int pageSize = 20);
    Task<IncomingPaymentDto?> GetPaymentByDocEntryAsync(int docEntry);
    Task<IncomingPaymentDto?> GetPaymentByDocNumAsync(int docNum);
    Task<IncomingPaymentDateResponse?> GetPaymentsByDateAsync(DateTime date);
    Task<IncomingPaymentDateResponse?> GetPaymentsByDateRangeAsync(DateTime fromDate, DateTime toDate);
    Task<IncomingPaymentDateResponse?> GetPaymentsByCustomerAsync(string cardCode);
}

public class PaymentService : IPaymentService
{
    private readonly HttpClient _httpClient;
    private readonly IIncomingPaymentCacheService _cacheService;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(
        HttpClient httpClient,
        IIncomingPaymentCacheService cacheService,
        ILogger<PaymentService> logger)
    {
        _httpClient = httpClient;
        _cacheService = cacheService;
        _logger = logger;
    }

    public async Task<IncomingPaymentListResponse?> GetPaymentsAsync(int page = 1, int pageSize = 20)
    {
        try
        {
            // Use cache service for faster loading
            return await _cacheService.GetCachedPaymentsAsync(page, pageSize);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting payments from cache, falling back to API");
            try
            {
                return await _httpClient.GetFromJsonAsync<IncomingPaymentListResponse>($"api/incomingpayment?page={page}&pageSize={pageSize}");
            }
            catch
            {
                return null;
            }
        }
    }

    public async Task<IncomingPaymentDto?> GetPaymentByDocEntryAsync(int docEntry)
    {
        try
        {
            // Use cache service
            return await _cacheService.GetCachedPaymentByDocEntryAsync(docEntry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting payment from cache, falling back to API");
            try
            {
                return await _httpClient.GetFromJsonAsync<IncomingPaymentDto>($"api/incomingpayment/{docEntry}");
            }
            catch
            {
                return null;
            }
        }
    }

    public async Task<IncomingPaymentDto?> GetPaymentByDocNumAsync(int docNum)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<IncomingPaymentDto>($"api/incomingpayment/docnum/{docNum}");
        }
        catch
        {
            return null;
        }
    }

    public async Task<IncomingPaymentDateResponse?> GetPaymentsByDateAsync(DateTime date)
    {
        try
        {
            // Use cache for date range query
            return await _cacheService.GetCachedPaymentsByDateRangeAsync(date, date);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting payments by date from cache, falling back to API");
            try
            {
                var dateStr = date.ToString("yyyy-MM-dd");
                return await _httpClient.GetFromJsonAsync<IncomingPaymentDateResponse>($"api/incomingpayment/date/{dateStr}");
            }
            catch
            {
                return null;
            }
        }
    }

    public async Task<IncomingPaymentDateResponse?> GetPaymentsByDateRangeAsync(DateTime fromDate, DateTime toDate)
    {
        try
        {
            // Use cache for date range query
            return await _cacheService.GetCachedPaymentsByDateRangeAsync(fromDate, toDate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting payments by date range from cache, falling back to API");
            try
            {
                var from = fromDate.ToString("yyyy-MM-dd");
                var to = toDate.ToString("yyyy-MM-dd");
                return await _httpClient.GetFromJsonAsync<IncomingPaymentDateResponse>($"api/incomingpayment/date/{from}/{to}");
            }
            catch
            {
                return null;
            }
        }
    }

    public async Task<IncomingPaymentDateResponse?> GetPaymentsByCustomerAsync(string cardCode)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<IncomingPaymentDateResponse>($"api/incomingpayment/customer/{cardCode}");
        }
        catch
        {
            return null;
        }
    }
}
