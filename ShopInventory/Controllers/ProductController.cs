using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class ProductController : ControllerBase
{
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly SAPSettings _settings;
    private readonly ILogger<ProductController> _logger;

    public ProductController(
        ISAPServiceLayerClient sapClient,
        IOptions<SAPSettings> settings,
        ILogger<ProductController> logger)
    {
        _sapClient = sapClient;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Gets all products/items from SAP
    /// </summary>
    /// <returns>List of all products</returns>
    [HttpGet]
    [ProducesResponseType(typeof(ProductsListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetAllProducts(CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new { message = "SAP integration is disabled" });
            }

            var items = await _sapClient.GetAllItemsAsync(cancellationToken);

            var products = items.Select(item => new ProductDto
            {
                ItemCode = item.ItemCode,
                ItemName = item.ItemName,
                BarCode = item.BarCode,
                ItemType = item.ItemType,
                ManagesBatches = item.ManageBatchNumbers == "tYES",
                DefaultWarehouse = item.DefaultWarehouse
            }).ToList();

            var response = new ProductsListResponseDto
            {
                Count = products.Count,
                Products = products
            };

            _logger.LogInformation("Retrieved {Count} products from SAP", products.Count);
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
            _logger.LogError(ex, "Error retrieving all products");
            return StatusCode(500, new { message = "Error retrieving products", error = ex.Message });
        }
    }

    /// <summary>
    /// Gets all products in a warehouse with their batch information
    /// </summary>
    /// <param name="warehouseCode">The warehouse code</param>
    /// <returns>List of products with batches</returns>
    [HttpGet("warehouse/{warehouseCode}")]
    [ProducesResponseType(typeof(WarehouseProductsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetProductsInWarehouse(
        string warehouseCode,
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

            // Get items in warehouse
            var items = await _sapClient.GetItemsInWarehouseAsync(warehouseCode, cancellationToken);

            // Get all batch numbers in the warehouse
            var allBatches = await _sapClient.GetAllBatchNumbersInWarehouseAsync(warehouseCode, cancellationToken);

            // Group batches by item code for easy lookup
            var batchesByItem = allBatches.GroupBy(b => b.ItemCode)
                .ToDictionary(g => g.Key ?? string.Empty, g => g.ToList());

            // Map to DTOs
            var products = items.Select(item => MapToProductDto(item, batchesByItem)).ToList();

            var response = new WarehouseProductsResponseDto
            {
                WarehouseCode = warehouseCode,
                TotalProducts = products.Count,
                ProductsWithBatches = products.Count(p => p.Batches?.Count > 0),
                Products = products
            };

            _logger.LogInformation("Retrieved {Count} products in warehouse {Warehouse}, {BatchCount} with batches",
                response.TotalProducts, warehouseCode, response.ProductsWithBatches);

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
            _logger.LogError(ex, "Error retrieving products for warehouse {Warehouse}", warehouseCode);
            return StatusCode(500, new { message = "Error retrieving products", error = ex.Message });
        }
    }

    /// <summary>
    /// Gets products in a warehouse with pagination
    /// </summary>
    /// <param name="warehouseCode">The warehouse code</param>
    /// <param name="page">Page number (default: 1)</param>
    /// <param name="pageSize">Number of records per page (default: 20, max: 100)</param>
    /// <returns>List of products with pagination info</returns>
    [HttpGet("warehouse/{warehouseCode}/paged")]
    [ProducesResponseType(typeof(WarehouseProductsPagedResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPagedProductsInWarehouse(
        string warehouseCode,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
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

            if (page < 1)
            {
                return BadRequest(new { message = "Page number must be at least 1" });
            }

            if (pageSize < 1 || pageSize > 100)
            {
                return BadRequest(new { message = "Page size must be between 1 and 100" });
            }

            // Get paged items in warehouse
            var items = await _sapClient.GetPagedItemsInWarehouseAsync(warehouseCode, page, pageSize, cancellationToken);

            // Get batch numbers for the retrieved items
            var itemCodes = items.Select(i => i.ItemCode).Where(c => c != null).ToList();
            var allBatches = await _sapClient.GetAllBatchNumbersInWarehouseAsync(warehouseCode, cancellationToken);

            // Filter batches to only those for our items
            var relevantBatches = allBatches.Where(b => itemCodes.Contains(b.ItemCode)).ToList();
            var batchesByItem = relevantBatches.GroupBy(b => b.ItemCode)
                .ToDictionary(g => g.Key ?? string.Empty, g => g.ToList());

            // Map to DTOs
            var products = items.Select(item => MapToProductDto(item, batchesByItem)).ToList();

            var response = new WarehouseProductsPagedResponseDto
            {
                WarehouseCode = warehouseCode,
                Page = page,
                PageSize = pageSize,
                Count = products.Count,
                HasMore = products.Count == pageSize,
                Products = products
            };

            _logger.LogInformation("Retrieved page {Page} of products in warehouse {Warehouse} ({Count} records)",
                page, warehouseCode, products.Count);

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
            _logger.LogError(ex, "Error retrieving paged products for warehouse {Warehouse}", warehouseCode);
            return StatusCode(500, new { message = "Error retrieving products", error = ex.Message });
        }
    }

    /// <summary>
    /// Gets batch information for a specific product in a warehouse
    /// </summary>
    /// <param name="warehouseCode">The warehouse code</param>
    /// <param name="itemCode">The item/product code</param>
    /// <returns>Product with batch details</returns>
    [HttpGet("warehouse/{warehouseCode}/item/{itemCode}/batches")]
    [ProducesResponseType(typeof(ProductBatchesResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetProductBatchesInWarehouse(
        string warehouseCode,
        string itemCode,
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

            if (string.IsNullOrWhiteSpace(itemCode))
            {
                return BadRequest(new { message = "Item code is required" });
            }

            // Get item details
            var item = await _sapClient.GetItemByCodeAsync(itemCode, cancellationToken);

            if (item == null)
            {
                return NotFound(new { message = $"Item with code '{itemCode}' not found" });
            }

            // Get batches for this item in the warehouse
            var batches = await _sapClient.GetBatchNumbersForItemInWarehouseAsync(itemCode, warehouseCode, cancellationToken);

            var batchDtos = batches.Select(MapToBatchDto).ToList();

            var response = new ProductBatchesResponseDto
            {
                WarehouseCode = warehouseCode,
                ItemCode = item.ItemCode,
                ItemName = item.ItemName,
                TotalQuantity = batches.Sum(b => b.Quantity),
                BatchCount = batches.Count,
                Batches = batchDtos
            };

            _logger.LogInformation("Retrieved {BatchCount} batches for item {ItemCode} in warehouse {Warehouse}",
                response.BatchCount, itemCode, warehouseCode);

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
            _logger.LogError(ex, "Error retrieving batches for item {ItemCode} in warehouse {Warehouse}", itemCode, warehouseCode);
            return StatusCode(500, new { message = "Error retrieving product batches", error = ex.Message });
        }
    }

    /// <summary>
    /// Gets a product by its item code
    /// </summary>
    /// <param name="itemCode">The item/product code</param>
    /// <returns>Product details</returns>
    [HttpGet("{itemCode}")]
    [ProducesResponseType(typeof(ProductDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetProductByCode(
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

            var item = await _sapClient.GetItemByCodeAsync(itemCode, cancellationToken);

            if (item == null)
            {
                return NotFound(new { message = $"Item with code '{itemCode}' not found" });
            }

            var product = new ProductDto
            {
                ItemCode = item.ItemCode,
                ItemName = item.ItemName,
                BarCode = item.BarCode,
                ItemType = item.ItemType,
                ManagesBatches = item.ManageBatchNumbers == "tYES",
                QuantityInStock = item.QuantityOnStock,
                QuantityAvailable = item.QuantityOnStock - item.QuantityOrderedByCustomers,
                QuantityCommitted = item.QuantityOrderedByCustomers,
                UoM = item.InventoryUOM
            };

            return Ok(product);
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
            _logger.LogError(ex, "Error retrieving item {ItemCode}", itemCode);
            return StatusCode(500, new { message = "Error retrieving product", error = ex.Message });
        }
    }

    #region Helper Methods

    private ProductDto MapToProductDto(Item item, Dictionary<string, List<BatchNumber>> batchesByItem)
    {
        var itemBatches = batchesByItem.TryGetValue(item.ItemCode ?? string.Empty, out var batches)
            ? batches
            : new List<BatchNumber>();

        return new ProductDto
        {
            ItemCode = item.ItemCode,
            ItemName = item.ItemName,
            BarCode = item.BarCode,
            ItemType = item.ItemType,
            ManagesBatches = item.ManageBatchNumbers == "tYES",
            QuantityInStock = item.QuantityOnStock,
            QuantityAvailable = item.QuantityOnStock - item.QuantityOrderedByCustomers,
            QuantityCommitted = item.QuantityOrderedByCustomers,
            UoM = item.InventoryUOM,
            Batches = itemBatches.Select(MapToBatchDto).ToList()
        };
    }

    private BatchDto MapToBatchDto(BatchNumber batch)
    {
        return new BatchDto
        {
            BatchNumber = batch.BatchNum,
            Quantity = batch.Quantity,
            Status = batch.Status,
            ExpiryDate = batch.ExpiryDate,
            ManufacturingDate = batch.ManufacturingDate,
            AdmissionDate = batch.AdmissionDate,
            Location = batch.Location,
            Notes = batch.Notes
        };
    }

    #endregion
}
