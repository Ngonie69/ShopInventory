using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

public class CreateDesktopInvoiceRequest
{
    public string? ExternalReferenceId { get; set; }
    public string? SourceSystem { get; set; }

    [Required(ErrorMessage = "Customer code is required")]
    public string CardCode { get; set; } = string.Empty;

    public string? CardName { get; set; }
    public string? DocDate { get; set; }
    public string? DocDueDate { get; set; }
    public string? NumAtCard { get; set; }
    public string? Comments { get; set; }
    public string? DocCurrency { get; set; }
    public int? SalesPersonCode { get; set; }
    public bool Fiscalize { get; set; } = true;

    [Required(ErrorMessage = "At least one line item is required")]
    [MinLength(1)]
    public List<CreateDesktopInvoiceLineRequest> Lines { get; set; } = new();
}
