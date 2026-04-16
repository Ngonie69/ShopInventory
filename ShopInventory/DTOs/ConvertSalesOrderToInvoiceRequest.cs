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
    public List<CreateDesktopInvoiceLineRequest>? Lines { get; set; }
}
