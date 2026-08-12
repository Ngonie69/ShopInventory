using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

public class ConvertSalesOrderToInvoiceRequest
{
    [Required(ErrorMessage = "Sales order ID is required")]
    public int SalesOrderId { get; set; }

    public string? ExternalReferenceId { get; set; }
    public string? SourceSystem { get; set; }
    public string? DocDate { get; set; }
    public string? DocDueDate { get; set; }
    public string? NumAtCard { get; set; }
    public string? Comments { get; set; }
    public string? DocCurrency { get; set; }
    public int? SalesPersonCode { get; set; }
    public bool Fiscalize { get; set; } = true;

    /// <summary>
    /// How the customer paid, as a brand. Same purpose as on the direct invoice: a van sale invoiced
    /// through this path is still a van sale and still owes the day's takings a tender.
    /// </summary>
    [MaxLength(50)]
    public string? PaymentMethod { get; set; }
    public List<CreateDesktopInvoiceLineRequest>? Lines { get; set; }
}
