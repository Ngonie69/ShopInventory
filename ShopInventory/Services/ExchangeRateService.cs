using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using System.Text.Json;

namespace ShopInventory.Services;

/// <summary>
/// Service implementation for Exchange Rate operations - fetches rates from SAP Business One
/// </summary>
public class ExchangeRateService : IExchangeRateService
{
    private readonly ApplicationDbContext _context;
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly ILogger<ExchangeRateService> _logger;
    private readonly IConfiguration _configuration;

    // Local currency in SAP - typically the company's base currency
    private readonly string _localCurrency;

    public ExchangeRateService(
        ApplicationDbContext context,
        ISAPServiceLayerClient sapClient,
        ILogger<ExchangeRateService> logger,
        IConfiguration configuration)
    {
        _context = context;
        _sapClient = sapClient;
        _logger = logger;
        _configuration = configuration;
        _localCurrency = configuration["SAP:LocalCurrency"] ?? "ZIG";
    }

    public async Task<ExchangeRateDto?> GetCurrentRateAsync(string fromCurrency, string toCurrency, CancellationToken cancellationToken = default)
    {
        try
        {
            // If both currencies are the same, return rate of 1
            if (fromCurrency.Equals(toCurrency, StringComparison.OrdinalIgnoreCase))
            {
                return new ExchangeRateDto
                {
                    FromCurrency = fromCurrency,
                    ToCurrency = toCurrency,
                    Rate = 1m,
                    InverseRate = 1m,
                    EffectiveDate = DateTime.UtcNow,
                    Source = "SAP",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };
            }

            // SAP stores rates against local currency, so we need to handle conversions
            if (fromCurrency.Equals(_localCurrency, StringComparison.OrdinalIgnoreCase))
            {
                // From local currency to foreign - get rate for toCurrency
                var sapRate = await _sapClient.GetExchangeRateAsync(toCurrency, DateTime.Today, cancellationToken);
                if (sapRate != null && sapRate.Rate > 0)
                {
                    return new ExchangeRateDto
                    {
                        FromCurrency = fromCurrency,
                        ToCurrency = toCurrency,
                        Rate = 1m / sapRate.Rate,  // Inverse because SAP stores foreign:local
                        InverseRate = sapRate.Rate,
                        EffectiveDate = sapRate.RateDate,
                        Source = "SAP",
                        IsActive = true,
                        CreatedAt = sapRate.RateDate
                    };
                }
            }
            else if (toCurrency.Equals(_localCurrency, StringComparison.OrdinalIgnoreCase))
            {
                // From foreign currency to local - get rate directly
                var sapRate = await _sapClient.GetExchangeRateAsync(fromCurrency, DateTime.Today, cancellationToken);
                if (sapRate != null && sapRate.Rate > 0)
                {
                    return new ExchangeRateDto
                    {
                        FromCurrency = fromCurrency,
                        ToCurrency = toCurrency,
                        Rate = sapRate.Rate,
                        InverseRate = 1m / sapRate.Rate,
                        EffectiveDate = sapRate.RateDate,
                        Source = "SAP",
                        IsActive = true,
                        CreatedAt = sapRate.RateDate
                    };
                }
            }
            else
            {
                // Cross-rate: Both are foreign currencies, calculate via local currency
                var fromRate = await _sapClient.GetExchangeRateAsync(fromCurrency, DateTime.Today, cancellationToken);
                var toRate = await _sapClient.GetExchangeRateAsync(toCurrency, DateTime.Today, cancellationToken);

                if (fromRate != null && toRate != null && fromRate.Rate > 0 && toRate.Rate > 0)
                {
                    var crossRate = fromRate.Rate / toRate.Rate;
                    return new ExchangeRateDto
                    {
                        FromCurrency = fromCurrency,
                        ToCurrency = toCurrency,
                        Rate = crossRate,
                        InverseRate = 1m / crossRate,
                        EffectiveDate = fromRate.RateDate > toRate.RateDate ? fromRate.RateDate : toRate.RateDate,
                        Source = "SAP",
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow
                    };
                }
            }

            _logger.LogWarning("Exchange rate not found in SAP for {FromCurrency}/{ToCurrency}", fromCurrency, toCurrency);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching exchange rate from SAP for {FromCurrency}/{ToCurrency}", fromCurrency, toCurrency);
            return null;
        }
    }

    public async Task<List<ExchangeRateDto>> GetAllActiveRatesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var sapRates = await _sapClient.GetExchangeRatesAsync(cancellationToken);
            var rates = new List<ExchangeRateDto>();

            foreach (var sapRate in sapRates)
            {
                if (sapRate.Rate <= 0) continue;

                // Add rate from foreign currency to local currency
                rates.Add(new ExchangeRateDto
                {
                    FromCurrency = sapRate.Currency,
                    ToCurrency = _localCurrency,
                    Rate = sapRate.Rate,
                    InverseRate = 1m / sapRate.Rate,
                    EffectiveDate = sapRate.RateDate,
                    Source = "SAP",
                    IsActive = true,
                    CreatedAt = sapRate.RateDate
                });

                // Also add the inverse rate (local to foreign)
                rates.Add(new ExchangeRateDto
                {
                    FromCurrency = _localCurrency,
                    ToCurrency = sapRate.Currency,
                    Rate = 1m / sapRate.Rate,
                    InverseRate = sapRate.Rate,
                    EffectiveDate = sapRate.RateDate,
                    Source = "SAP",
                    IsActive = true,
                    CreatedAt = sapRate.RateDate
                });
            }

            _logger.LogInformation("Fetched {Count} exchange rates from SAP", rates.Count);
            return rates.OrderBy(r => r.FromCurrency).ThenBy(r => r.ToCurrency).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching exchange rates from SAP");
            return new List<ExchangeRateDto>();
        }
    }

    public async Task<ExchangeRateHistoryDto> GetRateHistoryAsync(string fromCurrency, string toCurrency, int days = 30, CancellationToken cancellationToken = default)
    {
        // SAP doesn't easily provide historical rates via Service Layer
        // Return current rate as single item in history
        var currentRate = await GetCurrentRateAsync(fromCurrency, toCurrency, cancellationToken);

        return new ExchangeRateHistoryDto
        {
            FromCurrency = fromCurrency,
            ToCurrency = toCurrency,
            History = currentRate != null ? new List<ExchangeRateDto> { currentRate } : new List<ExchangeRateDto>()
        };
    }

    public Task<ExchangeRateDto> UpsertRateAsync(UpsertExchangeRateRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        // Exchange rates should be managed in SAP, not in the web app
        _logger.LogWarning("UpsertRateAsync called but exchange rates are managed in SAP. Use SAP to update rates.");
        throw new InvalidOperationException("Exchange rates are managed in SAP Business One. Please update rates directly in SAP.");
    }

    public async Task<decimal> ConvertAsync(decimal amount, string fromCurrency, string toCurrency, CancellationToken cancellationToken = default)
    {
        if (fromCurrency.Equals(toCurrency, StringComparison.OrdinalIgnoreCase))
            return amount;

        var rate = await GetCurrentRateAsync(fromCurrency, toCurrency, cancellationToken);

        if (rate != null)
            return amount * rate.Rate;

        throw new InvalidOperationException($"Exchange rate not found in SAP for {fromCurrency}/{toCurrency}");
    }

    public Task FetchExternalRatesAsync(CancellationToken cancellationToken = default)
    {
        // Exchange rates come from SAP, not external APIs
        _logger.LogInformation("FetchExternalRatesAsync called but rates are sourced from SAP");
        return Task.CompletedTask;
    }
}
