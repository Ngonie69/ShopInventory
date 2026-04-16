using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

public class DesktopBatchRequest
{
    [Required]
    public string BatchNumber { get; set; } = string.Empty;

    [Range(0.000001, double.MaxValue)]
    public decimal Quantity { get; set; }
}
