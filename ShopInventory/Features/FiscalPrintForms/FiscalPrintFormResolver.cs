using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.FiscalPrintForms;

/// <summary>
/// Reads a partner's print form from <c>BusinessPartnerFiscalPrintForms</c>.
/// </summary>
/// <remarks>
/// A read that fails falls back to A4 rather than failing the sale: the print form only changes how the
/// receipt is laid out, and a sale that goes unfiscalised over it costs far more than one printed on the
/// wrong paper.
/// </remarks>
public sealed class FiscalPrintFormResolver(
    ApplicationDbContext db,
    ILogger<FiscalPrintFormResolver> logger) : IFiscalPrintFormResolver
{
    public async Task<ReceiptPrintForm> ResolveAsync(
        string? sourceSystem, string? cardCode, CancellationToken cancellationToken)
    {
        var code = cardCode?.Trim();

        if (!SaleSourceSystems.ChoosesFiscalPrintForm(sourceSystem) || string.IsNullOrEmpty(code))
        {
            return ReceiptPrintForm.InvoiceA4;
        }

        try
        {
            var chosen = await db.BusinessPartnerFiscalPrintForms
                .AsNoTracking()
                .Where(row => row.CardCode == code)
                .Select(row => (ReceiptPrintForm?)row.PrintForm)
                .FirstOrDefaultAsync(cancellationToken);

            return chosen ?? ReceiptPrintForm.InvoiceA4;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Could not read the fiscal print form for {CardCode}; fiscalising as an A4 invoice", code);
            return ReceiptPrintForm.InvoiceA4;
        }
    }
}
