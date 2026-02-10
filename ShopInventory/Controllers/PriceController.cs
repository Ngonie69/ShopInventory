using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
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

    /// <summary>
    /// Gets all price lists from local cache (fast response)
    /// Use forceRefresh=true to sync from SAP first
    /// </summary>
    /// <param name="forceRefresh">If true, forces a sync from SAP before returning results</param>
    /// <returns>List of price lists from cache</returns>
    [HttpGet("pricelists")]
    [ProducesResponseType(typeof(PriceListsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPriceLists([FromQuery] bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Check if we have cached data
            var cachedCount = await _context.PriceLists.CountAsync(cancellationToken);
            var lastSync = await _context.PriceLists
                .Where(p => p.LastSyncedAt.HasValue)
                .MaxAsync(p => (DateTime?)p.LastSyncedAt, cancellationToken);

            // If no cache, force refresh is requested, or cache is older than 24 hours, sync from SAP
            var cacheExpiry = TimeSpan.FromHours(24);
            var needsSync = forceRefresh ||
                           cachedCount == 0 ||
                           !lastSync.HasValue ||
                           lastSync.Value < DateTime.UtcNow.Subtract(cacheExpiry);

            if (needsSync && _settings.Enabled)
            {
                _logger.LogInformation("Syncing price lists from SAP (forceRefresh={ForceRefresh}, cacheCount={Count}, lastSync={LastSync})",
                    forceRefresh, cachedCount, lastSync);

                try
                {
                    await SyncPriceListsFromSAPAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to sync price lists from SAP, returning cached data");
                    // If sync fails but we have cached data, return that
                    if (cachedCount > 0)
                    {
                        _logger.LogInformation("Returning {Count} cached price lists after SAP sync failure", cachedCount);
                    }
                    else
                    {
                        throw; // No cached data and sync failed, propagate the error
                    }
                }
            }

            // Return cached data
            var priceLists = await _context.PriceLists
                .Where(p => p.IsActive)
                .OrderBy(p => p.ListNum)
                .Select(p => new PriceListDto
                {
                    ListNum = p.ListNum,
                    ListName = p.ListName,
                    BasePriceList = p.BasePriceList.HasValue ? p.BasePriceList.Value.ToString() : null,
                    Currency = p.Currency,
                    IsActive = p.IsActive,
                    Factor = p.Factor,
                    RoundingMethod = p.RoundingMethod
                })
                .ToListAsync(cancellationToken);

            stopwatch.Stop();

            var response = new PriceListsResponseDto
            {
                TotalCount = priceLists.Count,
                PriceLists = priceLists
            };

            _logger.LogInformation("Retrieved {Count} price lists in {Elapsed}ms (last sync: {LastSync}, needsSync: {NeedsSync})",
                response.TotalCount, stopwatch.ElapsedMilliseconds, lastSync?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Never", needsSync);

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
            _logger.LogError(ex, "Error retrieving price lists");
            return StatusCode(500, new { message = "Error retrieving price lists", error = ex.Message });
        }
    }

    /// <summary>
    /// Forces a sync of price lists from SAP to local cache
    /// </summary>
    /// <returns>Sync result with count of synced price lists</returns>
    [HttpPost("pricelists/sync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SyncPriceLists(CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new { message = "SAP integration is disabled" });
            }

            var syncedCount = await SyncPriceListsFromSAPAsync(cancellationToken);

            return Ok(new
            {
                message = "Price lists synced successfully",
                count = syncedCount,
                syncedAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing price lists from SAP");
            return StatusCode(500, new { message = "Error syncing price lists", error = ex.Message });
        }
    }

    /// <summary>
    /// Internal method to sync price lists from SAP to local database
    /// </summary>
    private async Task<int> SyncPriceListsFromSAPAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting price list sync from SAP...");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var sapPriceLists = await _sapClient.GetPriceListsAsync(cancellationToken);
        var syncTime = DateTime.UtcNow;

        foreach (var sapList in sapPriceLists)
        {
            var existing = await _context.PriceLists
                .FirstOrDefaultAsync(p => p.ListNum == sapList.ListNum, cancellationToken);

            if (existing != null)
            {
                // Update existing
                existing.ListName = sapList.ListName;
                existing.Currency = sapList.Currency;
                existing.Factor = sapList.Factor;
                existing.RoundingMethod = sapList.RoundingMethod;
                existing.IsActive = sapList.IsActive;
                existing.UpdatedAt = syncTime;
                existing.LastSyncedAt = syncTime;
            }
            else
            {
                // Add new
                _context.PriceLists.Add(new Models.Entities.PriceListEntity
                {
                    ListNum = sapList.ListNum,
                    ListName = sapList.ListName,
                    Currency = sapList.Currency,
                    Factor = sapList.Factor,
                    RoundingMethod = sapList.RoundingMethod,
                    IsActive = sapList.IsActive,
                    CreatedAt = syncTime,
                    LastSyncedAt = syncTime
                });
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation("Price list sync completed: {Count} lists synced in {Elapsed}ms",
            sapPriceLists.Count, stopwatch.ElapsedMilliseconds);

        return sapPriceLists.Count;
    }

    /// <summary>
    /// Gets all item prices from a specific price list (cached)
    /// Use forceRefresh=true to sync from SAP first
    /// </summary>
    /// <param name="priceListNum">The price list number</param>
    /// <param name="forceRefresh">If true, syncs from SAP before returning data</param>
    /// <returns>List of item prices from the specified price list</returns>
    [HttpGet("pricelists/{priceListNum:int}/items")]
    [ProducesResponseType(typeof(ItemPricesByListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPricesByPriceList(
        int priceListNum,
        [FromQuery] bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new { message = "SAP integration is disabled" });
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Check if we have cached prices for this price list
            var cachedPrices = await _context.ItemPrices
                .Where(p => p.PriceList == priceListNum && p.SyncedFromSAP)
                .ToListAsync(cancellationToken);

            var cacheExpiry = TimeSpan.FromHours(1);
            var oldestAllowed = DateTime.UtcNow.Subtract(cacheExpiry);

            // If forceRefresh or cache is empty or stale, sync from SAP
            if (forceRefresh || cachedPrices.Count == 0 ||
                cachedPrices.Any(p => p.LastSyncedAt == null || p.LastSyncedAt < oldestAllowed))
            {
                _logger.LogInformation("Syncing prices for price list {PriceListNum} from SAP (forceRefresh={ForceRefresh}, cacheCount={CacheCount})",
                    priceListNum, forceRefresh, cachedPrices.Count);

                await SyncItemPricesFromSAPAsync(priceListNum, cancellationToken);

                // Re-fetch from cache after sync
                cachedPrices = await _context.ItemPrices
                    .Where(p => p.PriceList == priceListNum && p.SyncedFromSAP)
                    .ToListAsync(cancellationToken);
            }

            stopwatch.Stop();

            if (cachedPrices.Count == 0)
            {
                return NotFound(new { message = $"No prices found for price list {priceListNum}" });
            }

            var firstItem = cachedPrices.First();
            var prices = cachedPrices.Select(p => new ItemPriceByListDto
            {
                ItemCode = p.ItemCode,
                ItemName = p.ItemName,
                ForeignName = p.ForeignName,
                Price = p.Price,
                PriceListNum = p.PriceList,
                PriceListName = p.PriceListName,
                Currency = p.Currency
            }).ToList();

            var response = new ItemPricesByListResponseDto
            {
                TotalCount = prices.Count,
                PriceListNum = priceListNum,
                PriceListName = firstItem.PriceListName,
                Currency = firstItem.Currency,
                Prices = prices
            };

            _logger.LogInformation("Retrieved {Count} item prices from price list {PriceListNum} ({PriceListName}) in {Elapsed}ms (cached)",
                response.TotalCount, priceListNum, response.PriceListName, stopwatch.ElapsedMilliseconds);

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
            _logger.LogError(ex, "Error retrieving prices from price list {PriceListNum}", priceListNum);
            return StatusCode(500, new { message = "Error retrieving price list items", error = ex.Message });
        }
    }

    /// <summary>
    /// Syncs item prices for a specific price list from SAP to local database
    /// </summary>
    [HttpPost("pricelists/{priceListNum:int}/sync")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SyncItemPricesForPriceList(int priceListNum, CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new { message = "SAP integration is disabled" });
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var count = await SyncItemPricesFromSAPAsync(priceListNum, cancellationToken);
            stopwatch.Stop();

            return Ok(new
            {
                message = $"Successfully synced {count} item prices for price list {priceListNum}",
                count,
                priceListNum,
                elapsedMs = stopwatch.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing item prices for price list {PriceListNum}", priceListNum);
            return StatusCode(500, new { message = "Error syncing item prices", error = ex.Message });
        }
    }

    /// <summary>
    /// Internal method to sync item prices for a price list from SAP to local database
    /// </summary>
    private async Task<int> SyncItemPricesFromSAPAsync(int priceListNum, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("Starting item prices sync for price list {PriceListNum} from SAP...", priceListNum);

        var sapPrices = await _sapClient.GetPricesByPriceListAsync(priceListNum, cancellationToken);

        if (sapPrices.Count == 0)
        {
            _logger.LogWarning("No prices returned from SAP for price list {PriceListNum}", priceListNum);
            return 0;
        }

        var syncTime = DateTime.UtcNow;
        var priceListName = sapPrices.First().PriceListName;

        // Get existing cached prices for this price list
        var existingPrices = await _context.ItemPrices
            .Where(p => p.PriceList == priceListNum && p.SyncedFromSAP)
            .ToDictionaryAsync(p => p.ItemCode, cancellationToken);

        foreach (var sapPrice in sapPrices)
        {
            if (existingPrices.TryGetValue(sapPrice.ItemCode ?? "", out var existing))
            {
                // Update existing
                existing.ItemName = sapPrice.ItemName;
                existing.ForeignName = sapPrice.ForeignName;
                existing.Price = sapPrice.Price;
                existing.PriceListName = sapPrice.PriceListName;
                existing.Currency = sapPrice.Currency;
                existing.LastSyncedAt = syncTime;
                existing.UpdatedAt = syncTime;
            }
            else if (!string.IsNullOrEmpty(sapPrice.ItemCode))
            {
                // Add new
                _context.ItemPrices.Add(new ItemPriceEntity
                {
                    PriceList = priceListNum,
                    ItemCode = sapPrice.ItemCode,
                    ItemName = sapPrice.ItemName,
                    ForeignName = sapPrice.ForeignName,
                    Price = sapPrice.Price,
                    PriceListName = sapPrice.PriceListName,
                    Currency = sapPrice.Currency,
                    SyncedFromSAP = true,
                    CreatedAt = syncTime,
                    LastSyncedAt = syncTime
                });
            }
        }

        // Remove prices that no longer exist in SAP
        var sapItemCodes = sapPrices.Where(p => !string.IsNullOrEmpty(p.ItemCode)).Select(p => p.ItemCode!).ToHashSet();
        var toRemove = existingPrices.Values.Where(p => !sapItemCodes.Contains(p.ItemCode)).ToList();
        if (toRemove.Count > 0)
        {
            _context.ItemPrices.RemoveRange(toRemove);
            _logger.LogInformation("Removing {Count} stale item prices from price list {PriceListNum}", toRemove.Count, priceListNum);
        }

        await _context.SaveChangesAsync(cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation("Item prices sync for price list {PriceListNum} completed: {Count} prices synced in {Elapsed}ms",
            priceListNum, sapPrices.Count, stopwatch.ElapsedMilliseconds);

        return sapPrices.Count;
    }

    /// <summary>
    /// Gets the price for a specific item from a specific price list
    /// </summary>
    /// <param name="priceListNum">The price list number</param>
    /// <param name="itemCode">The item code</param>
    /// <returns>The item price from the specified price list</returns>
    [HttpGet("pricelists/{priceListNum:int}/items/{itemCode}")]
    [ProducesResponseType(typeof(ItemPriceByListDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetItemPriceFromList(int priceListNum, string itemCode, CancellationToken cancellationToken)
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

            var price = await _sapClient.GetItemPriceFromListAsync(itemCode, priceListNum, cancellationToken);

            if (price == null)
            {
                return NotFound(new { message = $"No price found for item '{itemCode}' in price list {priceListNum}" });
            }

            _logger.LogInformation("Retrieved price for item {ItemCode} from price list {PriceListNum}: {Price} {Currency}",
                itemCode, priceListNum, price.Price, price.Currency);

            return Ok(price);
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
            _logger.LogError(ex, "Error retrieving price for item {ItemCode} from price list {PriceListNum}", itemCode, priceListNum);
            return StatusCode(500, new { message = "Error retrieving item price from price list", error = ex.Message });
        }
    }
}
