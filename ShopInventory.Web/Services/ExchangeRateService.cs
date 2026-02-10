using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface IExchangeRateService
{
    Task<List<ExchangeRateDto>?> GetAllActiveRatesAsync();
    Task<ExchangeRateDto?> GetCurrentRateAsync(string fromCurrency, string toCurrency);
    Task<ExchangeRateHistoryResponse?> GetRateHistoryAsync(string fromCurrency, string toCurrency, int days = 30);
    Task<ExchangeRateDto?> UpsertRateAsync(UpsertExchangeRateRequest request);
    Task<decimal?> ConvertAsync(decimal amount, string fromCurrency, string toCurrency);
    Task<bool> FetchExternalRatesAsync();
}

public class ExchangeRateService : IExchangeRateService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ExchangeRateService> _logger;

    public ExchangeRateService(HttpClient httpClient, ILogger<ExchangeRateService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<ExchangeRateDto>?> GetAllActiveRatesAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<ExchangeRateDto>>("api/exchangerate");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching exchange rates");
            return null;
        }
    }

    public async Task<ExchangeRateDto?> GetCurrentRateAsync(string fromCurrency, string toCurrency)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<ExchangeRateDto>($"api/exchangerate/{fromCurrency}/{toCurrency}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching exchange rate {From}/{To}", fromCurrency, toCurrency);
            return null;
        }
    }

    public async Task<ExchangeRateHistoryResponse?> GetRateHistoryAsync(string fromCurrency, string toCurrency, int days = 30)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<ExchangeRateHistoryResponse>($"api/exchangerate/{fromCurrency}/{toCurrency}/history?days={days}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching exchange rate history {From}/{To}", fromCurrency, toCurrency);
            return null;
        }
    }

    public async Task<ExchangeRateDto?> UpsertRateAsync(UpsertExchangeRateRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/exchangerate", request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<ExchangeRateDto>();
            }
            _logger.LogWarning("Failed to upsert exchange rate: {StatusCode}", response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error upserting exchange rate");
            return null;
        }
    }

    public async Task<decimal?> ConvertAsync(decimal amount, string fromCurrency, string toCurrency)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<ConvertResponse>($"api/exchangerate/convert?amount={amount}&from={fromCurrency}&to={toCurrency}");
            return response?.ConvertedAmount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting {Amount} {From} to {To}", amount, fromCurrency, toCurrency);
            return null;
        }
    }

    public async Task<bool> FetchExternalRatesAsync()
    {
        try
        {
            var response = await _httpClient.PostAsync("api/exchangerate/fetch-external", null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching external exchange rates");
            return false;
        }
    }

    private class ConvertResponse
    {
        public decimal ConvertedAmount { get; set; }
    }
}
