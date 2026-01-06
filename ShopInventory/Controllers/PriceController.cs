using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class PriceController : ControllerBase
{
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly ApplicationDbContext _context;
    private readonly SAPSettings _settings;
    private readonly ILogger<PriceController> _logger;

    public PriceController(
        ISAPServiceLayerClient sapClient,
        ApplicationDbContext context,
        IOptions<SAPSettings> settings,
        ILogger<PriceController> logger)
    {
        _sapClient = sapClient;
        _context = context;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Gets all item prices from the local cache (synced from SAP every 5 minutes)
    /// This is the preferred endpoint for faster response times
    /// </summary>
    /// <returns>List of item prices from cache</returns>
    [HttpGet("cached")]
    [ProducesResponseType(typeof(ItemPricesResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetCachedPrices(CancellationToken cancellationToken)
    {
        try
        {
            var prices = await _context.ItemPrices
                .Where(p => p.IsActive && p.SyncedFromSAP)
                .OrderBy(p => p.ItemCode)
                .Select(p => new ItemPriceDto
                {
                    ItemCode = p.ItemCode,
                    ItemName = p.ItemName,
                    Price = p.Price,
                    Currency = p.Currency
                })
                .ToListAsync(cancellationToken);

            var response = new ItemPricesResponseDto
            {
                TotalCount = prices.Count,
                UsdPriceCount = prices.Count(p => p.Currency == "USD"),
                ZigPriceCount = prices.Count(p => p.Currency == "ZIG"),
                Prices = prices
            };

            // Get last sync time
            var lastSync = await _context.ItemPrices
                .Where(p => p.SyncedFromSAP && p.LastSyncedAt.HasValue)
                .MaxAsync(p => (DateTime?)p.LastSyncedAt, cancellationToken);

            _logger.LogInformation("Retrieved {Count} cached item prices. Last sync: {LastSync}",
                response.TotalCount, lastSync?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Never");

            return Ok(new
            {
                response.TotalCount,
                response.UsdPriceCount,
                response.ZigPriceCount,
                LastSyncedAt = lastSync,
                Prices = response.Prices
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving cached item prices");
            return StatusCode(500, new { message = "Error retrieving cached item prices", error = ex.Message });
        }
    }

    /// <summary>
    /// Gets all item prices from both USD and ZIG price lists
    /// </summary>
    /// <returns>List of item prices</returns>
    [HttpGet]
    [ProducesResponseType(typeof(ItemPricesResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetAllPrices(CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new { message = "SAP integration is disabled" });
            }

            var prices = await _sapClient.GetItemPricesAsync(cancellationToken);

            var response = new ItemPricesResponseDto
            {
                TotalCount = prices.Count,
                UsdPriceCount = prices.Count(p => p.Currency == "USD"),
                ZigPriceCount = prices.Count(p => p.Currency == "ZIG"),
                Prices = prices
            };

            _logger.LogInformation("Retrieved {Count} item prices ({UsdCount} USD, {ZigCount} ZIG)",
                response.TotalCount, response.UsdPriceCount, response.ZigPriceCount);

            return Ok(response);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return StatusCode(504, new { message = "Connection to SAP Service Layer timed out." });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return StatusCode(502, new { message = "Unable to connect to SAP Service Layer.", error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving item prices");
            return StatusCode(500, new { message = "Error retrieving item prices", error = ex.Message });
        }
    }

    /// <summary>
    /// Gets all item prices grouped by item code with both USD and ZIG prices
    /// </summary>
    /// <returns>List of items with both prices</returns>
    [HttpGet("grouped")]
    [ProducesResponseType(typeof(ItemPricesGroupedResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetGroupedPrices(CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new { message = "SAP integration is disabled" });
            }

            var prices = await _sapClient.GetItemPricesAsync(cancellationToken);

            // Group prices by item code
            var groupedItems = prices
                .GroupBy(p => p.ItemCode)
                .Select(g => new ItemPriceGroupedDto
                {
                    ItemCode = g.Key,
                    ItemName = g.First().ItemName,
                    UsdPrice = g.FirstOrDefault(p => p.Currency == "USD")?.Price,
                    ZigPrice = g.FirstOrDefault(p => p.Currency == "ZIG")?.Price
                })
                .OrderBy(i => i.ItemCode)
                .ToList();

            var response = new ItemPricesGroupedResponseDto
            {
                TotalItems = groupedItems.Count,
                Items = groupedItems
            };

            _logger.LogInformation("Retrieved {Count} items with grouped prices", response.TotalItems);

            return Ok(response);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return StatusCode(504, new { message = "Connection to SAP Service Layer timed out." });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return StatusCode(502, new { message = "Unable to connect to SAP Service Layer.", error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving grouped item prices");
            return StatusCode(500, new { message = "Error retrieving item prices", error = ex.Message });
        }
    }

    /// <summary>
    /// Gets prices for a specific item by item code
    /// </summary>
    /// <param name="itemCode">The item code</param>
    /// <returns>Item prices in both currencies</returns>
    [HttpGet("{itemCode}")]
    [ProducesResponseType(typeof(ItemPriceGroupedDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPriceByItemCode(
        string itemCode,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new { message = "SAP integration is disabled" });
            }

            if (string.IsNullOrWhiteSpace(itemCode))
            {
                return BadRequest(new { message = "Item code is required" });
            }

            var prices = await _sapClient.GetItemPriceByCodeAsync(itemCode, cancellationToken);

            if (prices.Count == 0)
            {
                return NotFound(new { message = $"No prices found for item '{itemCode}'" });
            }

            var response = new ItemPriceGroupedDto
            {
                ItemCode = itemCode,
                ItemName = prices.First().ItemName,
                UsdPrice = prices.FirstOrDefault(p => p.Currency == "USD")?.Price,
                ZigPrice = prices.FirstOrDefault(p => p.Currency == "ZIG")?.Price
            };

            _logger.LogInformation("Retrieved prices for item {ItemCode}: USD={UsdPrice}, ZIG={ZigPrice}",
                itemCode, response.UsdPrice, response.ZigPrice);

            return Ok(response);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return StatusCode(504, new { message = "Connection to SAP Service Layer timed out." });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return StatusCode(502, new { message = "Unable to connect to SAP Service Layer.", error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving prices for item {ItemCode}", itemCode);
            return StatusCode(500, new { message = "Error retrieving item prices", error = ex.Message });
        }
    }

    /// <summary>
    /// Gets prices filtered by currency
    /// </summary>
    /// <param name="currency">The currency code (USD or ZIG)</param>
    /// <returns>List of item prices for the specified currency</returns>
    [HttpGet("currency/{currency}")]
    [ProducesResponseType(typeof(ItemPricesResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPricesByCurrency(
        string currency,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new { message = "SAP integration is disabled" });
            }

            var normalizedCurrency = currency.ToUpperInvariant();
            if (normalizedCurrency != "USD" && normalizedCurrency != "ZIG")
            {
                return BadRequest(new { message = "Currency must be 'USD' or 'ZIG'" });
            }

            var allPrices = await _sapClient.GetItemPricesAsync(cancellationToken);
            var filteredPrices = allPrices.Where(p => p.Currency == normalizedCurrency).ToList();

            var response = new ItemPricesResponseDto
            {
                TotalCount = filteredPrices.Count,
                UsdPriceCount = normalizedCurrency == "USD" ? filteredPrices.Count : 0,
                ZigPriceCount = normalizedCurrency == "ZIG" ? filteredPrices.Count : 0,
                Prices = filteredPrices
            };

            _logger.LogInformation("Retrieved {Count} item prices for currency {Currency}",
                response.TotalCount, normalizedCurrency);

            return Ok(response);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return StatusCode(504, new { message = "Connection to SAP Service Layer timed out." });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return StatusCode(502, new { message = "Unable to connect to SAP Service Layer.", error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving item prices for currency {Currency}", currency);
            return StatusCode(500, new { message = "Error retrieving item prices", error = ex.Message });
        }
    }
}
