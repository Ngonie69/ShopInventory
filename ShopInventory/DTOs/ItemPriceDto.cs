namespace ShopInventory.DTOs;

/// <summary>
/// DTO for item price information
/// </summary>
public class ItemPriceDto
{
    public string? ItemCode { get; set; }
    public string? ItemName { get; set; }
    public decimal Price { get; set; }
    public string? Currency { get; set; }
}

/// <summary>
/// DTO for item prices response
/// </summary>
public class ItemPricesResponseDto
{
    public int TotalCount { get; set; }
    public int UsdPriceCount { get; set; }
    public int ZigPriceCount { get; set; }
    public List<ItemPriceDto>? Prices { get; set; }
}

/// <summary>
/// DTO for item prices grouped by item code
/// </summary>
public class ItemPriceGroupedDto
{
    public string? ItemCode { get; set; }
    public string? ItemName { get; set; }
    public decimal? UsdPrice { get; set; }
    public decimal? ZigPrice { get; set; }
}

/// <summary>
/// DTO for grouped item prices response
/// </summary>
public class ItemPricesGroupedResponseDto
{
    public int TotalItems { get; set; }
    public List<ItemPriceGroupedDto>? Items { get; set; }
}
