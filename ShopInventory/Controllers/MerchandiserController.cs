using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
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

    public MerchandiserController(ApplicationDbContext context, ILogger<MerchandiserController> logger, ISAPServiceLayerClient sapClient)
    {
        _context = context;
        _logger = logger;
        _sapClient = sapClient;
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
    /// Mobile endpoint: Get active products for the authenticated merchandiser
    /// </summary>
    [HttpGet("mobile/active-products")]
    [ProducesResponseType(typeof(List<MerchandiserActiveProductDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetActiveProductsForMobile(CancellationToken cancellationToken)
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

        // Join with product data for full details
        var products = await _context.Products
            .AsNoTracking()
            .Where(p => activeItemCodes.Contains(p.ItemCode) && p.IsActive)
            .Select(p => new MerchandiserActiveProductDto
            {
                ItemCode = p.ItemCode,
                ItemName = p.ItemName,
                BarCode = p.BarCode,
                UoM = p.SalesUnit ?? p.InventoryUOM,
                QuantityOnStock = p.QuantityOnStock,
                Price = p.Prices.Where(pr => pr.PriceList == 1).Select(pr => pr.Price).FirstOrDefault()
            })
            .OrderBy(p => p.ItemName)
            .ToListAsync(cancellationToken);

        return Ok(products);
    }

    /// <summary>
    /// Mobile endpoint: Get active products for a specific merchandiser by user ID
    /// </summary>
    [HttpGet("{userId:guid}/active-products")]
    [ProducesResponseType(typeof(List<MerchandiserActiveProductDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetActiveProductsForMerchandiser(Guid userId, CancellationToken cancellationToken)
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

        // Join with product data for full details
        var products = await _context.Products
            .AsNoTracking()
            .Where(p => activeItemCodes.Contains(p.ItemCode) && p.IsActive)
            .Select(p => new MerchandiserActiveProductDto
            {
                ItemCode = p.ItemCode,
                ItemName = p.ItemName,
                BarCode = p.BarCode,
                UoM = p.SalesUnit ?? p.InventoryUOM,
                QuantityOnStock = p.QuantityOnStock,
                Price = p.Prices.Where(pr => pr.PriceList == 1).Select(pr => pr.Price).FirstOrDefault()
            })
            .OrderBy(p => p.ItemName)
            .ToListAsync(cancellationToken);

        return Ok(products);
    }
}
