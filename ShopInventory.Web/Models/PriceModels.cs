namespace ShopInventory.Web.Models;

public class ItemPriceDto
{
    public string? ItemCode { get; set; }
    public string? ItemName { get; set; }
    public decimal Price { get; set; }
    public string? Currency { get; set; }
    public int? PriceListNum { get; set; }
    public string? PriceListName { get; set; }
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

/// <summary>
/// DTO for SAP Price List
/// </summary>
public class PriceListDto
{
    public int ListNum { get; set; }
    public string? ListName { get; set; }
    public string? BasePriceList { get; set; }
    public string? Currency { get; set; }
    public bool IsActive { get; set; }
    public decimal? Factor { get; set; }
    public string? RoundingMethod { get; set; }
}

/// <summary>
/// Response DTO for price lists
/// </summary>
public class PriceListsResponse
{
    public int TotalCount { get; set; }
    public List<PriceListDto>? PriceLists { get; set; }
}

/// <summary>
/// DTO for item price by price list
/// </summary>
public class ItemPriceByListDto
{
    public string? ItemCode { get; set; }
    public string? ItemName { get; set; }
    /// <summary>
    /// Foreign name from OITM.FrgnName - used for fiscalisation
    /// </summary>
    public string? ForeignName { get; set; }
    public decimal Price { get; set; }
    public int PriceListNum { get; set; }
    public string? PriceListName { get; set; }
    public string? Currency { get; set; }
}

/// <summary>
/// Response DTO for item prices by price list
/// </summary>
public class ItemPricesByListResponse
{
    public int TotalCount { get; set; }
    public int PriceListNum { get; set; }
    public string? PriceListName { get; set; }
    public string? Currency { get; set; }
    public List<ItemPriceByListDto>? Prices { get; set; }
}
