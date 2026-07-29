namespace ShopInventory.DTOs;

public class BusinessPartnerSpecialPriceDto
{
    public string CardCode { get; set; } = string.Empty;
    public string ItemCode { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public bool IsActive { get; set; }
}