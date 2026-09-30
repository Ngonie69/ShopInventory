using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.FiscalPrintForms;

/// <summary>
/// Decides whether a sale is fiscalised as a 48 mm receipt or an A4 invoice.
/// </summary>
public interface IFiscalPrintFormResolver
{
    /// <summary>
    /// The partner's chosen form for a till, vending or van sale; <see cref="ReceiptPrintForm.InvoiceA4"/>
    /// for any other source, for a partner with no choice saved, and when the choice cannot be read.
    /// </summary>
    Task<ReceiptPrintForm> ResolveAsync(string? sourceSystem, string? cardCode, CancellationToken cancellationToken);
}
