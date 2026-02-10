using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for Exchange Rate operations
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ExchangeRateController : ControllerBase
{
    private readonly IExchangeRateService _exchangeRateService;
    private readonly ILogger<ExchangeRateController> _logger;

    public ExchangeRateController(
        IExchangeRateService exchangeRateService,
        ILogger<ExchangeRateController> logger)
    {
        _exchangeRateService = exchangeRateService;
        _logger = logger;
    }

    /// <summary>
    /// Get all active exchange rates
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<ExchangeRateDto>>> GetAllActive(CancellationToken cancellationToken)
    {
        try
        {
            var rates = await _exchangeRateService.GetAllActiveRatesAsync(cancellationToken);
            return Ok(rates);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching exchange rates");
            return StatusCode(500, "Failed to fetch exchange rates");
        }
    }

    /// <summary>
    /// Get current exchange rate for a currency pair
    /// </summary>
    [HttpGet("{fromCurrency}/{toCurrency}")]
    public async Task<ActionResult<ExchangeRateDto>> GetCurrentRate(
        string fromCurrency,
        string toCurrency,
        CancellationToken cancellationToken)
    {
        try
        {
            var rate = await _exchangeRateService.GetCurrentRateAsync(fromCurrency, toCurrency, cancellationToken);
            if (rate == null)
            {
                return NotFound($"Exchange rate not found for {fromCurrency}/{toCurrency}");
            }
            return Ok(rate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching exchange rate {FromCurrency}/{ToCurrency}", fromCurrency, toCurrency);
            return StatusCode(500, "Failed to fetch exchange rate");
        }
    }

    /// <summary>
    /// Get exchange rate history for a currency pair
    /// </summary>
    [HttpGet("{fromCurrency}/{toCurrency}/history")]
    public async Task<ActionResult<ExchangeRateHistoryDto>> GetRateHistory(
        string fromCurrency,
        string toCurrency,
        [FromQuery] int days = 30,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var history = await _exchangeRateService.GetRateHistoryAsync(fromCurrency, toCurrency, days, cancellationToken);
            return Ok(history);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching exchange rate history {FromCurrency}/{ToCurrency}", fromCurrency, toCurrency);
            return StatusCode(500, "Failed to fetch exchange rate history");
        }
    }

    /// <summary>
    /// Convert an amount between currencies
    /// </summary>
    [HttpGet("convert")]
    public async Task<ActionResult<ConvertResponse>> Convert(
        [FromQuery] decimal amount,
        [FromQuery] string from,
        [FromQuery] string to,
        CancellationToken cancellationToken)
    {
        try
        {
            var converted = await _exchangeRateService.ConvertAsync(amount, from, to, cancellationToken);
            return Ok(new ConvertResponse
            {
                Amount = amount,
                FromCurrency = from,
                ToCurrency = to,
                ConvertedAmount = converted,
                Rate = amount > 0 ? converted / amount : 0
            });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting {Amount} {From} to {To}", amount, from, to);
            return StatusCode(500, "Failed to convert currency");
        }
    }
}

/// <summary>
/// Response for currency conversion
/// </summary>
public class ConvertResponse
{
    public decimal Amount { get; set; }
    public string FromCurrency { get; set; } = null!;
    public string ToCurrency { get; set; } = null!;
    public decimal ConvertedAmount { get; set; }
    public decimal Rate { get; set; }
}
