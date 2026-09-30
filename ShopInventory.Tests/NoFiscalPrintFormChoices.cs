using ShopInventory.Features.FiscalPrintForms;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>A resolver with no partner choices saved: every sale is an A4 invoice.</summary>
public sealed class NoFiscalPrintFormChoices : IFiscalPrintFormResolver
{
    public static readonly NoFiscalPrintFormChoices Instance = new();

    public Task<ReceiptPrintForm> ResolveAsync(
        string? sourceSystem, string? cardCode, CancellationToken cancellationToken)
        => Task.FromResult(ReceiptPrintForm.InvoiceA4);
}
