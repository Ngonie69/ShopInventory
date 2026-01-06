namespace ShopInventory.Web.Models;

public class ItemPriceDto
{
    public string? ItemCode { get; set; }
    public string? ItemName { get; set; }
    public decimal Price { get; set; }
    public string? Currency { get; set; }
}

public class ItemPricesResponse
{
    public int TotalCount { get; set; }
    public int UsdPriceCount { get; set; }
    public int ZigPriceCount { get; set; }
    public List<ItemPriceDto>? Prices { get; set; }
}

public class ItemPriceGroupedDto
{
    public string? ItemCode { get; set; }
    public string? ItemName { get; set; }
    public decimal? UsdPrice { get; set; }
    public decimal? ZigPrice { get; set; }
}

public class ItemPricesGroupedResponse
{
    public int TotalItems { get; set; }
    public List<ItemPriceGroupedDto>? Items { get; set; }
}
