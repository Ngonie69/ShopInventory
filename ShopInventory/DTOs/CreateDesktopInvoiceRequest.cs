using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class CreateDesktopInvoiceRequest
{
    public string? ExternalReferenceId { get; set; }
    public string? SourceSystem { get; set; }

    [Required(ErrorMessage = "Customer code is required")]
    public string CardCode { get; set; } = string.Empty;

    public string? CardName { get; set; }

    /// <summary>
    /// The route customer a van sold to, when the caller is the van sales app.
    ///
    /// <c>[JsonIgnore]</c> because these are set in-process by
    /// <c>CreateVanSalesDirectInvoiceHandler</c>, from a customer it has already checked the van is
    /// assigned to. Nothing downstream re-validates them, so a value bound from a request body would be
    /// an unchecked claim about who bought — the desktop endpoints that share this DTO must not be able
    /// to make one.
    /// </summary>
    [JsonIgnore]
    public int? RouteCustomerId { get; set; }

    [JsonIgnore]
    [MaxLength(50)]
    public string? RouteCustomerCode { get; set; }

    [JsonIgnore]
    [MaxLength(200)]
    public string? RouteCustomerName { get; set; }

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
