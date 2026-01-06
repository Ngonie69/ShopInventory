using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class StockController : ControllerBase
{
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly SAPSettings _settings;
    private readonly ILogger<StockController> _logger;

    public StockController(
        ISAPServiceLayerClient sapClient,
        IOptions<SAPSettings> settings,
        ILogger<StockController> logger)
    {
        _sapClient = sapClient;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Gets all available warehouses (supports dynamically created warehouses)
    /// </summary>
    /// <returns>List of warehouses</returns>
    [HttpGet("warehouses")]
    [ProducesResponseType(typeof(WarehouseListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetWarehouses(CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new { message = "SAP integration is disabled" });
            }

            var warehouses = await _sapClient.GetWarehousesAsync(cancellationToken);

            var response = new WarehouseListResponseDto
            {
                TotalWarehouses = warehouses.Count,
                Warehouses = warehouses
            };

            _logger.LogInformation("Retrieved {Count} warehouses", response.TotalWarehouses);

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
            _logger.LogError(ex, "Error retrieving warehouses");
            return StatusCode(500, new { message = "Error retrieving warehouses", error = ex.Message });
        }
    }

    /// <summary>
    /// Gets just the warehouse codes (simplified endpoint)
    /// </summary>
    /// <param name="includeInactive">Include inactive warehouses (default: false)</param>
    /// <returns>List of warehouse codes</returns>
    [HttpGet("warehouse-codes")]
    [ProducesResponseType(typeof(IEnumerable<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetWarehouseCodes(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new { message = "SAP integration is disabled" });
            }

            var warehouses = await _sapClient.GetWarehousesAsync(cancellationToken);
            var warehouseCodes = warehouses
                .Where(w => includeInactive || w.IsActive)
                .Select(w => w.WarehouseCode)
                .ToList();

            _logger.LogInformation("Retrieved {Count} warehouse codes (includeInactive: {IncludeInactive})",
                warehouseCodes.Count, includeInactive);

            return Ok(warehouseCodes);
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
            _logger.LogError(ex, "Error retrieving warehouse codes");
            return StatusCode(500, new { message = "Error retrieving warehouse codes", error = ex.Message });
        }
    }

    /// <summary>
    /// Gets stock quantities for all items in a warehouse
    /// </summary>
    /// <param name="warehouseCode">The warehouse code (supports any existing or new warehouse)</param>
    /// <param name="includePackagingStock">Include stock quantities for packaging materials (default: true)</param>
    /// <returns>List of stock quantities</returns>
    [HttpGet("warehouse/{warehouseCode}")]
    [ProducesResponseType(typeof(WarehouseStockResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetStockInWarehouse(
        string warehouseCode,
        [FromQuery] bool includePackagingStock = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new { message = "SAP integration is disabled" });
            }

            if (string.IsNullOrWhiteSpace(warehouseCode))
            {
                return BadRequest(new { message = "Warehouse code is required" });
            }

            var stockItems = await _sapClient.GetStockQuantitiesInWarehouseAsync(warehouseCode, cancellationToken);

            // Fetch packaging material stock if requested
            if (includePackagingStock)
            {
                await PopulatePackagingMaterialStock(stockItems, warehouseCode, cancellationToken);
            }

            var response = new WarehouseStockResponseDto
            {
                WarehouseCode = warehouseCode,
                TotalItems = stockItems.Count,
                ItemsInStock = stockItems.Count(s => s.InStock > 0),
                QueryDate = DateTime.UtcNow,
                Items = stockItems
            };

            _logger.LogInformation("Retrieved {Count} stock items in warehouse {Warehouse}, {InStock} with stock",
                response.TotalItems, warehouseCode, response.ItemsInStock);

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
            _logger.LogError(ex, "Error retrieving stock for warehouse {Warehouse}", warehouseCode);
            return StatusCode(500, new { message = "Error retrieving stock quantities", error = ex.Message });
        }
    }

    /// <summary>
    /// Populates packaging material stock for items that have packaging codes
    /// </summary>
    private async Task PopulatePackagingMaterialStock(
        List<StockQuantityDto> stockItems,
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        // Collect all unique packaging codes
        var packagingCodes = new HashSet<string>();

        foreach (var item in stockItems)
        {
            if (!string.IsNullOrWhiteSpace(item.PackagingCode))
                packagingCodes.Add(item.PackagingCode);
            if (!string.IsNullOrWhiteSpace(item.PackagingCodeLabels))
                packagingCodes.Add(item.PackagingCodeLabels);
            if (!string.IsNullOrWhiteSpace(item.PackagingCodeLids))
                packagingCodes.Add(item.PackagingCodeLids);
        }

        if (packagingCodes.Count == 0)
            return;

        // Fetch stock for all packaging materials in one query
        var packagingStock = await _sapClient.GetPackagingMaterialStockAsync(packagingCodes, warehouseCode, cancellationToken);

        // Assign packaging stock to each item
        foreach (var item in stockItems)
        {
            if (!string.IsNullOrWhiteSpace(item.PackagingCode) && packagingStock.TryGetValue(item.PackagingCode, out var pkgStock))
            {
                item.PackagingMaterialStock = pkgStock;
            }
            if (!string.IsNullOrWhiteSpace(item.PackagingCodeLabels) && packagingStock.TryGetValue(item.PackagingCodeLabels, out var lblStock))
            {
                item.PackagingLabelStock = lblStock;
            }
            if (!string.IsNullOrWhiteSpace(item.PackagingCodeLids) && packagingStock.TryGetValue(item.PackagingCodeLids, out var lidStock))
            {
                item.PackagingLidStock = lidStock;
            }
        }

        _logger.LogInformation("Populated packaging stock for {Count} unique packaging codes", packagingCodes.Count);
    }

    /// <summary>
    /// Gets stock quantities for items in a warehouse with pagination
    /// </summary>
    /// <param name="warehouseCode">The warehouse code</param>
    /// <param name="page">Page number (default: 1)</param>
    /// <param name="pageSize">Page size (default: 50, max: 200)</param>
    /// <returns>Paginated list of stock quantities</returns>
    [HttpGet("warehouse/{warehouseCode}/paged")]
    [ProducesResponseType(typeof(WarehouseStockPagedResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetStockInWarehousePaged(
        string warehouseCode,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new { message = "SAP integration is disabled" });
            }

            if (string.IsNullOrWhiteSpace(warehouseCode))
            {
                return BadRequest(new { message = "Warehouse code is required" });
            }

            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 50;
            if (pageSize > 200) pageSize = 200;

            var stockItems = await _sapClient.GetPagedStockQuantitiesInWarehouseAsync(
                warehouseCode, page, pageSize, cancellationToken);

            var response = new WarehouseStockPagedResponseDto
            {
                WarehouseCode = warehouseCode,
                Page = page,
                PageSize = pageSize,
                Count = stockItems.Count,
                HasMore = stockItems.Count == pageSize,
                QueryDate = DateTime.UtcNow,
                Items = stockItems
            };

            _logger.LogInformation("Retrieved page {Page} of stock items in warehouse {Warehouse}, count: {Count}",
                page, warehouseCode, response.Count);

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
            _logger.LogError(ex, "Error retrieving paged stock for warehouse {Warehouse}", warehouseCode);
            return StatusCode(500, new { message = "Error retrieving stock quantities", error = ex.Message });
        }
    }

    /// <summary>
    /// Gets quantities sold in a warehouse over a selected period
    /// </summary>
    /// <param name="warehouseCode">The warehouse code</param>
    /// <param name="fromDate">Start date of the period (format: yyyy-MM-dd)</param>
    /// <param name="toDate">End date of the period (format: yyyy-MM-dd)</param>
    /// <returns>List of sales quantities by item</returns>
    [HttpGet("warehouse/{warehouseCode}/sales")]
    [ProducesResponseType(typeof(WarehouseSalesResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetSalesInWarehouse(
        string warehouseCode,
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new { message = "SAP integration is disabled" });
            }

            if (string.IsNullOrWhiteSpace(warehouseCode))
            {
                return BadRequest(new { message = "Warehouse code is required" });
            }

            if (fromDate == default)
            {
                return BadRequest(new { message = "From date is required" });
            }

            if (toDate == default)
            {
                return BadRequest(new { message = "To date is required" });
            }

            if (fromDate > toDate)
            {
                return BadRequest(new { message = "From date cannot be greater than to date" });
            }

            var salesItems = await _sapClient.GetSalesQuantitiesByWarehouseAsync(
                warehouseCode, fromDate, toDate, cancellationToken);

            var response = new WarehouseSalesResponseDto
            {
                WarehouseCode = warehouseCode,
                FromDate = fromDate,
                ToDate = toDate,
                TotalItemsSold = salesItems.Count,
                TotalSalesValue = salesItems.Sum(s => s.TotalSalesValue),
                TotalInvoices = salesItems.Sum(s => s.InvoiceCount),
                Items = salesItems
            };

            _logger.LogInformation("Retrieved {Count} sales items in warehouse {Warehouse} from {From} to {To}",
                response.TotalItemsSold, warehouseCode, fromDate.ToString("yyyy-MM-dd"), toDate.ToString("yyyy-MM-dd"));

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
            _logger.LogError(ex, "Error retrieving sales for warehouse {Warehouse}", warehouseCode);
            return StatusCode(500, new { message = "Error retrieving sales quantities", error = ex.Message });
        }
    }

    /// <summary>
    /// Gets quantities sold in a warehouse over a selected period using POST request
    /// </summary>
    /// <param name="warehouseCode">The warehouse code</param>
    /// <param name="request">The date range request</param>
    /// <returns>List of sales quantities by item</returns>
    [HttpPost("warehouse/{warehouseCode}/sales")]
    [ProducesResponseType(typeof(WarehouseSalesResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetSalesInWarehousePost(
        string warehouseCode,
        [FromBody] SalesQueryRequestDto request,
        CancellationToken cancellationToken)
    {
        return await GetSalesInWarehouse(warehouseCode, request.FromDate, request.ToDate, cancellationToken);
    }
}
