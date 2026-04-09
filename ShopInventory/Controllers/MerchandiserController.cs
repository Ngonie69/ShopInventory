using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

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

    public MerchandiserController(ApplicationDbContext context, ILogger<MerchandiserController> logger)
    {
        _context = context;
        _logger = logger;
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
    /// Assign products to a merchandiser
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
    /// Update product active/inactive status for a merchandiser
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
