using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Authentication;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for merchandiser product management
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class MerchandiserController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<MerchandiserController> _logger;
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly ISalesOrderService _salesOrderService;

    public MerchandiserController(ApplicationDbContext context, ILogger<MerchandiserController> logger, ISAPServiceLayerClient sapClient, ISalesOrderService salesOrderService)
    {
        _context = context;
        _logger = logger;
        _sapClient = sapClient;
        _salesOrderService = salesOrderService;
    }

    /// <summary>
    /// Get all merchandisers with product assignment summary
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<MerchandiserSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMerchandisers(CancellationToken cancellationToken)
    {
        var merchandisers = await _context.Users
            .AsNoTracking()
            .Where(u => u.Role == "Merchandiser" && u.IsActive)
            .Select(u => new MerchandiserSummaryDto
            {
                UserId = u.Id,
                Username = u.Username,
                FirstName = u.FirstName,
                LastName = u.LastName,
                AssignedCustomers = string.IsNullOrEmpty(u.AssignedCustomerCodes) ? 0 :
                    u.AssignedCustomerCodes.Length - u.AssignedCustomerCodes.Replace(",", "").Length,
                TotalProducts = _context.MerchandiserProducts.Count(mp => mp.MerchandiserUserId == u.Id),
                ActiveProducts = _context.MerchandiserProducts.Count(mp => mp.MerchandiserUserId == u.Id && mp.IsActive)
            })
            .ToListAsync(cancellationToken);

        // Fix customer count - AssignedCustomerCodes is JSON array, count properly
        foreach (var m in merchandisers)
        {
            var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == m.UserId, cancellationToken);
            if (user != null)
            {
                m.AssignedCustomers = user.GetCustomerCodes().Count;
            }
        }

        return Ok(merchandisers);
    }

    /// <summary>
    /// Get products assigned to a specific merchandiser
    /// </summary>
    [HttpGet("{userId:guid}/products")]
    [ProducesResponseType(typeof(MerchandiserProductListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMerchandiserProducts(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _context.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && u.Role == "Merchandiser", cancellationToken);

        if (user == null)
        {
            return NotFound(new { message = "Merchandiser not found" });
        }

        var products = await _context.MerchandiserProducts
            .AsNoTracking()
            .Where(mp => mp.MerchandiserUserId == userId)
            .OrderBy(mp => mp.ItemCode)
            .Select(mp => new MerchandiserProductDto
            {
                Id = mp.Id,
                MerchandiserUserId = mp.MerchandiserUserId,
                ItemCode = mp.ItemCode,
                ItemName = mp.ItemName,
                IsActive = mp.IsActive,
                CreatedAt = mp.CreatedAt,
                UpdatedAt = mp.UpdatedAt,
                UpdatedBy = mp.UpdatedBy
            })
            .ToListAsync(cancellationToken);

        return Ok(new MerchandiserProductListResponseDto
        {
            MerchandiserUserId = userId,
            MerchandiserName = $"{user.FirstName} {user.LastName}".Trim(),
            TotalCount = products.Count,
            ActiveCount = products.Count(p => p.IsActive),
            Products = products
        });
    }

    /// <summary>
    /// Get global merchandiser products (uniform across all merchandisers)
    /// </summary>
    [HttpGet("products")]
    [ProducesResponseType(typeof(MerchandiserProductListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetGlobalProducts(CancellationToken cancellationToken)
    {
        // Backfill any missing item names from SAP
        var missingNames = await _context.MerchandiserProducts
            .Where(mp => mp.ItemName == null || mp.ItemName == "")
            .Select(mp => mp.ItemCode)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (missingNames.Count > 0)
        {
            try
            {
                // Build IN clause for specific missing items - query ALL items, not just U_SalesItem='Yes'
                var inClause = string.Join(",", missingNames.Select(c => $"'{c.Replace("'", "''")}'"));
                var sqlText = $"SELECT T0.\"ItemCode\", T0.\"ItemName\" FROM OITM T0 WHERE T0.\"ItemCode\" IN ({inClause}) ORDER BY T0.\"ItemCode\"";
                var rows = await _sapClient.ExecuteRawSqlQueryAsync("MerchBackfill", "Backfill Item Names", sqlText, cancellationToken);
                var nameMap = rows
                    .Where(r => r.GetValueOrDefault("ItemCode") != null)
                    .ToDictionary(
                        r => r["ItemCode"]!.ToString()!,
                        r => r.GetValueOrDefault("ItemName")?.ToString() ?? "",
                        StringComparer.OrdinalIgnoreCase);

                var toUpdate = await _context.MerchandiserProducts
                    .Where(mp => mp.ItemName == null || mp.ItemName == "")
                    .ToListAsync(cancellationToken);

                foreach (var mp in toUpdate)
                {
                    if (nameMap.TryGetValue(mp.ItemCode, out var name))
                    {
                        mp.ItemName = name;
                    }
                }
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Backfilled {Count} merchandiser product names from SAP", toUpdate.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to backfill item names from SAP");
            }
        }

        // Get distinct products across all merchandisers (they are uniform)
        var allProducts = await _context.MerchandiserProducts
            .AsNoTracking()
            .OrderBy(mp => mp.ItemCode)
            .ToListAsync(cancellationToken);

        var products = allProducts
            .GroupBy(mp => mp.ItemCode)
            .Select(g => g.OrderByDescending(mp => mp.UpdatedAt ?? mp.CreatedAt).First())
            .OrderBy(mp => mp.ItemCode)
            .Select(mp => new MerchandiserProductDto
            {
                Id = mp.Id,
                ItemCode = mp.ItemCode,
                ItemName = mp.ItemName,
                IsActive = mp.IsActive,
                CreatedAt = mp.CreatedAt,
                UpdatedAt = mp.UpdatedAt,
                UpdatedBy = mp.UpdatedBy
            })
            .ToList();

        return Ok(new MerchandiserProductListResponseDto
        {
            MerchandiserName = "All Merchandisers",
            TotalCount = products.Count,
            ActiveCount = products.Count(p => p.IsActive),
            Products = products
        });
    }

    /// <summary>
    /// Get sales items from SAP for assignment
    /// </summary>
    [HttpGet("sap-sales-items")]
    [ProducesResponseType(typeof(List<SapSalesItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSapSalesItems(CancellationToken cancellationToken)
    {
        var sqlText = "SELECT T0.\"ItemCode\", T0.\"ItemName\", T0.\"U_SalesItem\" FROM OITM T0 WHERE T0.\"U_SalesItem\" ='Yes' ORDER BY T0.\"ItemCode\"";

        var rows = await _sapClient.ExecuteRawSqlQueryAsync(
            "MerchSalesItems",
            "Merchandiser Sales Items",
            sqlText,
            cancellationToken);

        var items = rows.Select(r => new SapSalesItemDto
        {
            ItemCode = r.GetValueOrDefault("ItemCode")?.ToString() ?? "",
            ItemName = r.GetValueOrDefault("ItemName")?.ToString() ?? ""
        }).ToList();

        return Ok(items);
    }

    /// <summary>
    /// Assign products to all merchandisers (uniform assignment)
    /// </summary>
    [HttpPost("products")]
    [ProducesResponseType(typeof(MerchandiserProductListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AssignProductsGlobal([FromBody] AssignMerchandiserProductsRequest request, CancellationToken cancellationToken)
    {
        if (request.ItemCodes == null || request.ItemCodes.Count == 0)
        {
            return BadRequest(new { message = "At least one item code is required" });
        }

        var currentUsername = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";

        // Get all active merchandisers
        var merchandiserIds = await _context.Users
            .AsNoTracking()
            .Where(u => u.Role == "Merchandiser" && u.IsActive)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        // Use item names from request if provided, otherwise look up from local DB
        var productNames = request.ItemNames ?? new Dictionary<string, string>();
        if (productNames.Count == 0)
        {
            try
            {
                productNames = await _context.Products
                    .AsNoTracking()
                    .Where(p => request.ItemCodes.Contains(p.ItemCode))
                    .ToDictionaryAsync(p => p.ItemCode, p => p.ItemName ?? "", cancellationToken);
            }
            catch { /* proceed without names */ }
        }

        int totalAdded = 0;
        foreach (var merchandiserId in merchandiserIds)
        {
            var existing = await _context.MerchandiserProducts
                .Where(mp => mp.MerchandiserUserId == merchandiserId)
                .Select(mp => mp.ItemCode)
                .ToListAsync(cancellationToken);

            var newCodes = request.ItemCodes.Except(existing).ToList();

            var newEntities = newCodes.Select(code => new MerchandiserProductEntity
            {
                MerchandiserUserId = merchandiserId,
                ItemCode = code,
                ItemName = productNames.GetValueOrDefault(code),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedBy = currentUsername
            }).ToList();

            _context.MerchandiserProducts.AddRange(newEntities);
            totalAdded += newEntities.Count;
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Assigned {Count} products to {MerchCount} merchandisers", request.ItemCodes.Count, merchandiserIds.Count);

        return await GetGlobalProducts(cancellationToken);
    }

    /// <summary>
    /// Assign products to a specific merchandiser (legacy)
    /// </summary>
    [HttpPost("{userId:guid}/products")]
    [ProducesResponseType(typeof(MerchandiserProductListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AssignProducts(Guid userId, [FromBody] AssignMerchandiserProductsRequest request, CancellationToken cancellationToken)
    {
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Id == userId && u.Role == "Merchandiser", cancellationToken);

        if (user == null)
        {
            return NotFound(new { message = "Merchandiser not found" });
        }

        if (request.ItemCodes == null || request.ItemCodes.Count == 0)
        {
            return BadRequest(new { message = "At least one item code is required" });
        }

        var currentUsername = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";

        // Get existing assignments
        var existing = await _context.MerchandiserProducts
            .Where(mp => mp.MerchandiserUserId == userId)
            .Select(mp => mp.ItemCode)
            .ToListAsync(cancellationToken);

        // Only add new ones
        var newCodes = request.ItemCodes.Except(existing).ToList();

        // Look up product names
        var productNames = await _context.Products
            .AsNoTracking()
            .Where(p => newCodes.Contains(p.ItemCode))
            .ToDictionaryAsync(p => p.ItemCode, p => p.ItemName, cancellationToken);

        var newEntities = newCodes.Select(code => new MerchandiserProductEntity
        {
            MerchandiserUserId = userId,
            ItemCode = code,
            ItemName = productNames.GetValueOrDefault(code),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedBy = currentUsername
        }).ToList();

        _context.MerchandiserProducts.AddRange(newEntities);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Assigned {Count} products to merchandiser {UserId}", newCodes.Count, userId);

        return await GetMerchandiserProducts(userId, cancellationToken);
    }

    /// <summary>
    /// Remove products from all merchandisers globally
    /// </summary>
    [HttpDelete("products")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveProductsGlobal([FromBody] AssignMerchandiserProductsRequest request, CancellationToken cancellationToken)
    {
        var products = await _context.MerchandiserProducts
            .Where(mp => request.ItemCodes.Contains(mp.ItemCode))
            .ToListAsync(cancellationToken);

        _context.MerchandiserProducts.RemoveRange(products);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Removed {Count} product records globally for {ItemCount} items", products.Count, request.ItemCodes.Count);

        return Ok(new { message = $"{request.ItemCodes.Count} products removed from all merchandisers" });
    }

    /// <summary>
    /// Update product active/inactive status globally for all merchandisers
    /// </summary>
    [HttpPut("products/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateProductStatusGlobal([FromBody] UpdateMerchandiserProductStatusRequest request, CancellationToken cancellationToken)
    {
        var currentUsername = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";

        var products = await _context.MerchandiserProducts
            .Where(mp => request.ItemCodes.Contains(mp.ItemCode))
            .ToListAsync(cancellationToken);

        foreach (var product in products)
        {
            product.IsActive = request.IsActive;
            product.UpdatedAt = DateTime.UtcNow;
            product.UpdatedBy = currentUsername;
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Updated {Count} product records to {Status} globally",
            products.Count, request.IsActive ? "active" : "inactive");

        return Ok(new { message = $"{request.ItemCodes.Count} products updated to {(request.IsActive ? "active" : "inactive")} for all merchandisers" });
    }

    /// <summary>
    /// Update product active/inactive status for a specific merchandiser (legacy)
    /// </summary>
    [HttpPut("{userId:guid}/products/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateProductStatus(Guid userId, [FromBody] UpdateMerchandiserProductStatusRequest request, CancellationToken cancellationToken)
    {
        var user = await _context.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && u.Role == "Merchandiser", cancellationToken);

        if (user == null)
        {
            return NotFound(new { message = "Merchandiser not found" });
        }

        var currentUsername = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";

        var products = await _context.MerchandiserProducts
            .Where(mp => mp.MerchandiserUserId == userId && request.ItemCodes.Contains(mp.ItemCode))
            .ToListAsync(cancellationToken);

        foreach (var product in products)
        {
            product.IsActive = request.IsActive;
            product.UpdatedAt = DateTime.UtcNow;
            product.UpdatedBy = currentUsername;
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Updated {Count} products to {Status} for merchandiser {UserId}",
            products.Count, request.IsActive ? "active" : "inactive", userId);

        return Ok(new { message = $"{products.Count} products updated to {(request.IsActive ? "active" : "inactive")}" });
    }

    /// <summary>
    /// Remove products from a merchandiser
    /// </summary>
    [HttpDelete("{userId:guid}/products")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveProducts(Guid userId, [FromBody] AssignMerchandiserProductsRequest request, CancellationToken cancellationToken)
    {
        var products = await _context.MerchandiserProducts
            .Where(mp => mp.MerchandiserUserId == userId && request.ItemCodes.Contains(mp.ItemCode))
            .ToListAsync(cancellationToken);

        _context.MerchandiserProducts.RemoveRange(products);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Removed {Count} products from merchandiser {UserId}", products.Count, userId);

        return Ok(new { message = $"{products.Count} products removed" });
    }

    /// <summary>
    /// Mobile endpoint: Get distinct item categories (U_ItemGroup) for the merchandiser's assigned products
    /// </summary>
    [HttpGet("mobile/categories")]
    [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetProductCategories(CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Forbid();
        }

        var activeItemCodes = await _context.MerchandiserProducts
            .AsNoTracking()
            .Where(mp => mp.MerchandiserUserId == userId && mp.IsActive)
            .Select(mp => mp.ItemCode)
            .ToListAsync(cancellationToken);

        if (activeItemCodes.Count == 0)
        {
            return Ok(new List<string>());
        }

        try
        {
            var inClause = string.Join(",", activeItemCodes.Select(c => $"'{c.Replace("'", "''")}'"));
            var sqlText = $@"
                SELECT DISTINCT T0.""U_ItemGroup""
                FROM OITM T0
                WHERE T0.""ItemCode"" IN ({inClause})
                  AND T0.""U_ItemGroup"" IS NOT NULL
                  AND T0.""U_ItemGroup"" <> ''
                ORDER BY T0.""U_ItemGroup""";

            var rows = await _sapClient.ExecuteRawSqlQueryAsync(
                "MerchCategories", "Merchandiser Product Categories", sqlText, cancellationToken);

            var categories = rows
                .Select(r => r.GetValueOrDefault("U_ItemGroup")?.ToString())
                .Where(c => !string.IsNullOrEmpty(c))
                .ToList();

            return Ok(categories);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch product categories from SAP");
            return Ok(new List<string>());
        }
    }

    /// <summary>
    /// Mobile endpoint: Get active products for the authenticated merchandiser
    /// </summary>
    [HttpGet("mobile/active-products")]
    [ProducesResponseType(typeof(List<MerchandiserActiveProductDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetActiveProductsForMobile(
        [FromQuery] string? search = null,
        [FromQuery] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Forbid();
        }

        var user = await _context.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user == null)
        {
            return Forbid();
        }

        // Get active merchandiser products
        var activeItemCodes = await _context.MerchandiserProducts
            .AsNoTracking()
            .Where(mp => mp.MerchandiserUserId == userId && mp.IsActive)
            .Select(mp => mp.ItemCode)
            .ToListAsync(cancellationToken);

        if (activeItemCodes.Count == 0)
        {
            return Ok(new List<MerchandiserActiveProductDto>());
        }

        var products = await GetProductDetailsFromSAP(activeItemCodes, search: search, category: category, cancellationToken: cancellationToken);
        return Ok(products);
    }

    /// <summary>
    /// Mobile endpoint: Get active products for a specific merchandiser by user ID
    /// </summary>
    [HttpGet("{userId:guid}/active-products")]
    [ProducesResponseType(typeof(List<MerchandiserActiveProductDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetActiveProductsForMerchandiser(
        Guid userId,
        [FromQuery] string? search = null,
        [FromQuery] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var user = await _context.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user == null)
        {
            return NotFound(new { message = "User not found" });
        }

        // Get active merchandiser products
        var activeItemCodes = await _context.MerchandiserProducts
            .AsNoTracking()
            .Where(mp => mp.MerchandiserUserId == userId && mp.IsActive)
            .Select(mp => mp.ItemCode)
            .ToListAsync(cancellationToken);

        if (activeItemCodes.Count == 0)
        {
            return Ok(new List<MerchandiserActiveProductDto>());
        }

        var products = await GetProductDetailsFromSAP(activeItemCodes, search: search, category: category, cancellationToken: cancellationToken);
        return Ok(products);
    }

    /// <summary>
    /// Mobile endpoint: Get active products for the authenticated merchandiser, filtered for a specific customer.
    /// Returns only the merchandiser's assigned products with customer-specific pricing.
    /// Supports optional search by name or code.
    /// </summary>
    [HttpGet("mobile/customer/{cardCode}/products")]
    [ProducesResponseType(typeof(List<MerchandiserActiveProductDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetActiveProductsForCustomer(
        string cardCode,
        [FromQuery] string? search = null,
        [FromQuery] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Forbid();
        }

        var user = await _context.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user == null)
        {
            return Forbid();
        }

        // Get active merchandiser products from local DB
        var activeItemCodes = await _context.MerchandiserProducts
            .AsNoTracking()
            .Where(mp => mp.MerchandiserUserId == userId && mp.IsActive)
            .Select(mp => mp.ItemCode)
            .ToListAsync(cancellationToken);

        if (activeItemCodes.Count == 0)
        {
            return Ok(new List<MerchandiserActiveProductDto>());
        }

        var products = await GetProductDetailsFromSAP(activeItemCodes, cardCode, search, category, cancellationToken);
        return Ok(products);
    }

    /// <summary>
    /// Mobile endpoint: Submit a merchandiser order (customer requested quantities).
    /// Creates a sales order with Source = Mobile. Only items assigned to the merchandiser are allowed.
    /// </summary>
    [HttpPost("mobile/order")]
    [RequirePermission(Permission.CreateSalesOrders)]
    [ProducesResponseType(typeof(SalesOrderDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SubmitMobileOrder([FromBody] MerchandiserOrderRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Forbid();
        }

        // Validate that all items are in the merchandiser's assigned products
        var assignedItemCodes = await _context.MerchandiserProducts
            .AsNoTracking()
            .Where(mp => mp.MerchandiserUserId == userId && mp.IsActive)
            .Select(mp => mp.ItemCode)
            .ToListAsync(cancellationToken);

        var requestedItemCodes = request.Items.Select(i => i.ItemCode).ToList();
        var unassigned = requestedItemCodes.Except(assignedItemCodes, StringComparer.OrdinalIgnoreCase).ToList();
        if (unassigned.Count > 0)
        {
            return BadRequest(new { message = $"The following items are not assigned to you: {string.Join(", ", unassigned)}" });
        }

        // Map to CreateSalesOrderRequest
        var salesOrderRequest = new CreateSalesOrderRequest
        {
            CardCode = request.CardCode,
            CardName = request.CardName,
            DeliveryDate = request.DeliveryDate,
            Comments = request.Notes,
            Source = SalesOrderSource.Mobile,
            MerchandiserNotes = request.Notes,
            DeviceInfo = request.DeviceInfo,
            Lines = request.Items.Select(item => new CreateSalesOrderLineRequest
            {
                ItemCode = item.ItemCode,
                ItemDescription = item.ItemName,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice
            }).ToList()
        };

        try
        {
            var order = await _salesOrderService.CreateAsync(salesOrderRequest, userId, cancellationToken);
            _logger.LogInformation("Merchandiser {UserId} submitted mobile order {OrderNumber} for customer {CardCode} with {LineCount} items",
                userId, order.OrderNumber, request.CardCode, request.Items.Count);
            return CreatedAtAction(nameof(GetMobileOrders), new { }, order);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating merchandiser mobile order for customer {CardCode}", request.CardCode);
            return BadRequest(new { message = ex.InnerException?.Message ?? ex.Message });
        }
    }

    /// <summary>
    /// Mobile endpoint: Get orders submitted by the authenticated merchandiser
    /// </summary>
    [HttpGet("mobile/orders")]
    [ProducesResponseType(typeof(SalesOrderListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetMobileOrders(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] SalesOrderStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Forbid();
        }

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 50);

        var query = _context.SalesOrders
            .AsNoTracking()
            .Where(o => o.Source == SalesOrderSource.Mobile && o.CreatedByUserId == userId);

        if (status.HasValue)
            query = query.Where(o => o.Status == status.Value);

        var totalCount = await query.CountAsync(cancellationToken);
        var orders = await query
            .OrderByDescending(o => o.OrderDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(o => o.Lines)
            .Select(o => new SalesOrderDto
            {
                Id = o.Id,
                OrderNumber = o.OrderNumber,
                OrderDate = o.OrderDate,
                DeliveryDate = o.DeliveryDate,
                CardCode = o.CardCode,
                CardName = o.CardName,
                Status = o.Status,
                Comments = o.Comments,
                Currency = o.Currency,
                SubTotal = o.SubTotal,
                TaxAmount = o.TaxAmount,
                DocTotal = o.DocTotal,
                CreatedAt = o.CreatedAt,
                Source = o.Source,
                MerchandiserNotes = o.MerchandiserNotes,
                Lines = o.Lines.Select(l => new SalesOrderLineDto
                {
                    Id = l.Id,
                    LineNum = l.LineNum,
                    ItemCode = l.ItemCode,
                    ItemDescription = l.ItemDescription,
                    Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice,
                    LineTotal = l.LineTotal,
                    UoMCode = l.UoMCode
                }).ToList()
            })
            .ToListAsync(cancellationToken);

        return Ok(new SalesOrderListResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling((double)totalCount / pageSize),
            Orders = orders
        });
    }

    /// <summary>
    /// Fetch product details from SAP for the given item codes.
    /// Optionally resolves customer-specific pricing via their price list.
    /// Supports search filtering by item name or code.
    /// </summary>
    private async Task<List<MerchandiserActiveProductDto>> GetProductDetailsFromSAP(
        List<string> itemCodes,
        string? cardCode = null,
        string? search = null,
        string? category = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var inClause = string.Join(",", itemCodes.Select(c => $"'{c.Replace("'", "''")}'"));

            // Resolve customer price list if cardCode provided
            var priceListJoin = "LEFT JOIN ITM1 T1 ON T0.\"ItemCode\" = T1.\"ItemCode\" AND T1.\"PriceList\" = 1";
            if (!string.IsNullOrEmpty(cardCode))
            {
                var safeCardCode = cardCode.Replace("'", "''");
                priceListJoin = $@"LEFT JOIN OCRD T2 ON T2.""CardCode"" = '{safeCardCode}'
                LEFT JOIN ITM1 T1 ON T0.""ItemCode"" = T1.""ItemCode"" AND T1.""PriceList"" = COALESCE(T2.""ListNum"", 1)";
            }

            // Build optional search filter
            var searchFilter = "";
            if (!string.IsNullOrWhiteSpace(search))
            {
                var safeSearch = search.Replace("'", "''");
                searchFilter = $@" AND (LOWER(T0.""ItemCode"") LIKE LOWER('%{safeSearch}%') OR LOWER(T0.""ItemName"") LIKE LOWER('%{safeSearch}%') OR T0.""CodeBars"" LIKE '%{safeSearch}%')";
            }

            // Filter by U_ItemGroup (Item Category)
            var categoryFilter = "";
            if (!string.IsNullOrWhiteSpace(category))
            {
                var safeCategory = category.Replace("'", "''");
                categoryFilter = $@" AND T0.""U_ItemGroup"" = '{safeCategory}'";
            }

            var sqlText = $@"
                SELECT T0.""ItemCode"", T0.""ItemName"", T0.""CodeBars"" AS ""BarCode"",
                       T0.""SalUnitMsr"" AS ""UoM"",
                       T0.""InvntryUom"" AS ""InventoryUOM"",
                       T1.""Price""
                FROM OITM T0
                {priceListJoin}
                WHERE T0.""ItemCode"" IN ({inClause}){searchFilter}{categoryFilter}
                ORDER BY T0.""ItemName""";

            var rows = await _sapClient.ExecuteRawSqlQueryAsync(
                "MerchActiveProducts", "Merchandiser Active Products", sqlText, cancellationToken);

            return rows.Select(r => new MerchandiserActiveProductDto
            {
                ItemCode = r.GetValueOrDefault("ItemCode")?.ToString() ?? "",
                ItemName = r.GetValueOrDefault("ItemName")?.ToString(),
                BarCode = r.GetValueOrDefault("BarCode")?.ToString(),
                UoM = r.GetValueOrDefault("UoM")?.ToString() ?? r.GetValueOrDefault("InventoryUOM")?.ToString(),
                Price = decimal.TryParse(r.GetValueOrDefault("Price")?.ToString(), out var price) ? price : 0
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch merchandiser products from SAP, falling back to local DB");

            // Fallback: return item codes + names from MerchandiserProducts table
            var query = _context.MerchandiserProducts
                .AsNoTracking()
                .Where(mp => itemCodes.Contains(mp.ItemCode) && mp.IsActive);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.ToLower();
                query = query.Where(mp =>
                    mp.ItemCode.ToLower().Contains(s) ||
                    (mp.ItemName != null && mp.ItemName.ToLower().Contains(s)));
            }

            return await query
                .OrderBy(mp => mp.ItemName ?? mp.ItemCode)
                .Select(mp => new MerchandiserActiveProductDto
                {
                    ItemCode = mp.ItemCode,
                    ItemName = mp.ItemName
                })
                .ToListAsync(cancellationToken);
        }
    }
}
