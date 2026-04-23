using System.ComponentModel.DataAnnotations;
using ShopInventory.Common.Validation;

namespace ShopInventory.DTOs;

/// <summary>
/// Merchandiser product assignment DTO
/// </summary>
public class MerchandiserProductDto
{
    public int Id { get; set; }
    public Guid MerchandiserUserId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemName { get; set; }
    public string? Category { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// Response for merchandiser product list
/// </summary>
public class MerchandiserProductListResponseDto
{
    public Guid MerchandiserUserId { get; set; }
    public string? MerchandiserName { get; set; }
    public int TotalCount { get; set; }
    public int ActiveCount { get; set; }
    public List<MerchandiserProductDto> Products { get; set; } = new();
}

/// <summary>
/// Request to assign products to a merchandiser
/// </summary>
public class AssignMerchandiserProductsRequest
{
    /// <summary>
    /// List of item codes to assign
    /// </summary>
    public List<string> ItemCodes { get; set; } = new();

    /// <summary>
    /// Optional dictionary of item code to item name for bulk assignment
    /// </summary>
    public Dictionary<string, string>? ItemNames { get; set; }
}

/// <summary>
/// Request to update product active status for a merchandiser
/// </summary>
public class UpdateMerchandiserProductStatusRequest
{
    /// <summary>
    /// Item codes to update
    /// </summary>
    public List<string> ItemCodes { get; set; } = new();

    /// <summary>
    /// Set active or inactive
    /// </summary>
    public bool IsActive { get; set; }
}

/// <summary>
/// Merchandiser summary for listing
/// </summary>
public class MerchandiserSummaryDto
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string FullName => $"{FirstName} {LastName}".Trim();
    public int TotalProducts { get; set; }
    public int ActiveProducts { get; set; }
    public int AssignedCustomers { get; set; }
}

/// <summary>
/// Active product for mobile app consumption
/// </summary>
public class MerchandiserActiveProductDto
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemName { get; set; }
    public string? BarCode { get; set; }
    public decimal Price { get; set; }
    public string? UoM { get; set; }
    public string? Category { get; set; }
    public bool AllowDecimalQuantity => UomQuantityValidation.AllowDecimalQuantity(UoM);
}

/// <summary>
/// Paginated response for mobile active products endpoint
/// </summary>
public class MerchandiserActiveProductListResponseDto
{
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<MerchandiserActiveProductDto> Products { get; set; } = new();
}

/// <summary>
/// Sales item from SAP for merchandiser assignment
/// </summary>
public class SapSalesItemDto
{
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string? ItemGroup { get; set; }
}

/// <summary>
/// Mobile request to submit a merchandiser order (customer quantities)
/// </summary>
public class MerchandiserOrderRequest
{
    [Required(ErrorMessage = "Customer code is required")]
    public string CardCode { get; set; } = null!;

    public string? CardName { get; set; }

    public string? Notes { get; set; }

    public string? DeviceInfo { get; set; }

    public DateTime? DeliveryDate { get; set; }

    [Required(ErrorMessage = "At least one item is required")]
    [MinLength(1, ErrorMessage = "At least one item is required")]
    public List<MerchandiserOrderLineRequest> Items { get; set; } = new();
}

/// <summary>
/// Line item in a merchandiser order
/// </summary>
public class MerchandiserOrderLineRequest
{
    [Required(ErrorMessage = "Item code is required")]
    public string ItemCode { get; set; } = null!;

    public string? ItemName { get; set; }

    [Range(0.0001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public string? UoMCode { get; set; }
}
