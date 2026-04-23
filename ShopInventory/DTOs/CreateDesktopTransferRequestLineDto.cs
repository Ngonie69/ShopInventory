using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

public class CreateDesktopTransferRequestLineDto
{
    [Required(ErrorMessage = "Item code is required")]
    public string ItemCode { get; set; } = string.Empty;

    [Range(0.000001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    public string? UoMCode { get; set; }

    public string? FromWarehouseCode { get; set; }
    public string? ToWarehouseCode { get; set; }
}
