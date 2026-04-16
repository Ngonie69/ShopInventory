using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

public class CreateDesktopInvoiceLineRequest
{
    public int LineNum { get; set; }

    [Required(ErrorMessage = "Item code is required")]
    public string ItemCode { get; set; } = string.Empty;

    public string? ItemDescription { get; set; }

    [Range(0.000001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    public decimal? UnitPrice { get; set; }

    [Required(ErrorMessage = "Warehouse code is required")]
    public string WarehouseCode { get; set; } = string.Empty;

    public string? TaxCode { get; set; }
    public decimal? DiscountPercent { get; set; }
    public string? UoMCode { get; set; }
    public bool AutoAllocateBatches { get; set; } = true;
    public List<DesktopBatchRequest>? BatchNumbers { get; set; }
}
