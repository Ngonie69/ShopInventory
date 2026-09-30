using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Models.Entities;

/// <summary>
/// The fiscal document a business partner's till, vending and van sales are filed as: a 48 mm receipt or
/// an A4 invoice.
/// </summary>
/// <remarks>
/// A partner with no row gets <see cref="ReceiptPrintForm.InvoiceA4"/>, and so does every other channel
/// whatever a row says — see <see cref="Common.Sales.SaleSourceSystems.ChoosesFiscalPrintForm"/>.
/// </remarks>
[Table("BusinessPartnerFiscalPrintForms")]
public class BusinessPartnerFiscalPrintFormEntity
{
    [Key]
    [MaxLength(50)]
    public string CardCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? CardName { get; set; }

    public ReceiptPrintForm PrintForm { get; set; } = ReceiptPrintForm.InvoiceA4;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string? UpdatedBy { get; set; }
}
