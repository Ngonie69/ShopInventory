using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Service implementation for Exchange Rate operations - fetches rates from SAP Business One
/// </summary>
/// <remarks>
/// SAP holds one number per foreign currency and day, against its local currency, and a company
/// setting says which way that number reads. Production, read 2026-11-02: local currency USD, quoted
/// indirectly, so ZiG's 30 means 1 USD = 30 ZiG (order 86255 is ZiG 20,906.42 booked as USD 696.88).
/// This service used to assume a ZiG local currency and a direct quote, and showed that rate as
/// "1 ZiG = 30 ZIG". Both now come from SAP through
/// <see cref="ISAPServiceLayerClient.GetCurrencyRateSettingsAsync"/>.
/// </remarks>
public class ExchangeRateService : IExchangeRateService
{
    private const string RateSettingsCacheKey = "sap-currency-rate-settings";
    private static readonly TimeSpan RateSettingsCacheDuration = TimeSpan.FromHours(1);

    private readonly ApplicationDbContext _context;
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly ILogger<ExchangeRateService> _logger;
    private readonly IMemoryCache _cache;

    public ExchangeRateService(
        ApplicationDbContext context,
        ISAPServiceLayerClient sapClient,
        ILogger<ExchangeRateService> logger,
        IMemoryCache cache)
    {
        _context = context;
        _sapClient = sapClient;
        _logger = logger;
        _cache = cache;
    }

    private async Task<SapCurrencyRateSettings> GetRateSettingsAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(RateSettingsCacheKey, out SapCurrencyRateSettings? cached) && cached is not null)
        {
            return cached;
        }

        var settings = await _sapClient.GetCurrencyRateSettingsAsync(cancellationToken);
        _cache.Set(RateSettingsCacheKey, settings, RateSettingsCacheDuration);
        return settings;
    }

    private static bool SameCurrency(string a, string b) =>
        string.Equals(SapCurrencyCodes.ToSapCode(a), SapCurrencyCodes.ToSapCode(b), StringComparison.Ordinal);

    public async Task<ExchangeRateDto?> GetCurrentRateAsync(string fromCurrency, string toCurrency, CancellationToken cancellationToken = default)
    {
        try
        {
            if (SameCurrency(fromCurrency, toCurrency))
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

            var settings = await GetRateSettingsAsync(cancellationToken);
            var today = ExchangeRateMonitorDates.TodayCat(DateTime.UtcNow).ToDateTime(TimeOnly.MinValue);

            // One unit of the currency in local units, one local unit in the currency, and the date
            // of the SAP rate used. Both directions come straight from SAP's number, so 1 USD = 30 ZiG
            // reads as exactly 30 rather than as 1 / (1 / 30).
            async Task<(decimal LocalPer, decimal PerLocal, DateTime Date)?> AgainstLocalAsync(string currency)
            {
                if (SameCurrency(currency, settings.LocalCurrency))
                {
                    return (1m, 1m, today);
                }

                var sapRate = await _sapClient.GetExchangeRateAsync(currency, today, cancellationToken);
                return sapRate is { Rate: > 0 }
                    ? (settings.LocalPerForeign(sapRate.Rate), settings.ForeignPerLocal(sapRate.Rate), sapRate.RateDate)
                    : null;
            }

            var from = await AgainstLocalAsync(fromCurrency);
            var to = await AgainstLocalAsync(toCurrency);

            if (from is { } f && to is { } t)
            {
                var rate = f.LocalPer * t.PerLocal;
                var effective = SameCurrency(fromCurrency, settings.LocalCurrency) ? t.Date
                    : SameCurrency(toCurrency, settings.LocalCurrency) ? f.Date
                    : f.Date > t.Date ? f.Date : t.Date;

                return new ExchangeRateDto
                {
                    FromCurrency = fromCurrency,
                    ToCurrency = toCurrency,
                    Rate = rate,
                    InverseRate = rate == 0 ? 0 : 1m / rate,
                    EffectiveDate = effective,
                    Source = "SAP",
                    IsActive = true,
                    CreatedAt = effective
                };
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
            var settings = await GetRateSettingsAsync(cancellationToken);
            var sapRates = await _sapClient.GetExchangeRatesAsync(cancellationToken);
            var local = settings.LocalCurrency;
            var rates = new List<ExchangeRateDto>();

            foreach (var sapRate in sapRates)
            {
                if (sapRate.Rate <= 0 || SameCurrency(sapRate.Currency, local)) continue;

                var localPerForeign = settings.LocalPerForeign(sapRate.Rate);
                var foreignPerLocal = settings.ForeignPerLocal(sapRate.Rate);

                rates.Add(new ExchangeRateDto
                {
                    FromCurrency = sapRate.Currency,
                    ToCurrency = local,
                    Rate = localPerForeign,
                    InverseRate = foreignPerLocal,
                    EffectiveDate = sapRate.RateDate,
                    Source = "SAP",
                    IsActive = true,
                    CreatedAt = sapRate.RateDate
                });

                rates.Add(new ExchangeRateDto
                {
                    FromCurrency = local,
                    ToCurrency = sapRate.Currency,
                    Rate = foreignPerLocal,
                    InverseRate = localPerForeign,
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

    /// <summary>
    /// SAP's rate for each day, from what the rate monitor recorded, when one side is SAP's local
    /// currency and the other a watched currency. Otherwise today's rate alone.
    /// </summary>
    public async Task<ExchangeRateHistoryDto> GetRateHistoryAsync(string fromCurrency, string toCurrency, int days = 30, CancellationToken cancellationToken = default)
    {
        var history = new ExchangeRateHistoryDto { FromCurrency = fromCurrency, ToCurrency = toCurrency };

        try
        {
            var settings = await GetRateSettingsAsync(cancellationToken);
            var fromLocal = SameCurrency(fromCurrency, settings.LocalCurrency);
            var toLocal = SameCurrency(toCurrency, settings.LocalCurrency);

            if (fromLocal != toLocal)
            {
                var foreign = SapCurrencyCodes.ToSapCode(fromLocal ? toCurrency : fromCurrency);
                var since = ExchangeRateMonitorDates.TodayCat(DateTime.UtcNow).AddDays(-Math.Clamp(days, 1, 400));

                var rows = await _context.ExchangeRateObservations
                    .AsNoTracking()
                    .Where(o => o.Currency == foreign && o.SupersededAtUtc == null && o.RateDate >= since)
                    .OrderByDescending(o => o.RateDate)
                    .ToListAsync(cancellationToken);

                foreach (var row in rows)
                {
                    var rowSettings = new SapCurrencyRateSettings(row.LocalCurrency, null, row.IndirectQuotation);
                    var rate = fromLocal ? rowSettings.ForeignPerLocal(row.Rate) : rowSettings.LocalPerForeign(row.Rate);

                    history.History.Add(new ExchangeRateDto
                    {
                        FromCurrency = fromCurrency,
                        ToCurrency = toCurrency,
                        Rate = rate,
                        InverseRate = rate == 0 ? 0 : 1m / rate,
                        EffectiveDate = row.RateDate.ToDateTime(TimeOnly.MinValue),
                        Source = "SAP",
                        IsActive = true,
                        CreatedAt = row.FirstSeenAtUtc
                    });
                }

                if (history.History.Count > 0)
                {
                    return history;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the recorded rate history for {FromCurrency}/{ToCurrency}", fromCurrency, toCurrency);
        }

        var currentRate = await GetCurrentRateAsync(fromCurrency, toCurrency, cancellationToken);
        if (currentRate != null)
        {
            history.History.Add(currentRate);
        }

        return history;
    }

    public Task<ExchangeRateDto> UpsertRateAsync(UpsertExchangeRateRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        // Exchange rates should be managed in SAP, not in the web app
        _logger.LogWarning("UpsertRateAsync called but exchange rates are managed in SAP. Use SAP to update rates.");
        throw new InvalidOperationException("Exchange rates are managed in SAP Business One. Please update rates directly in SAP.");
    }

    public async Task<decimal> ConvertAsync(decimal amount, string fromCurrency, string toCurrency, CancellationToken cancellationToken = default)
    {
        if (SameCurrency(fromCurrency, toCurrency))
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
