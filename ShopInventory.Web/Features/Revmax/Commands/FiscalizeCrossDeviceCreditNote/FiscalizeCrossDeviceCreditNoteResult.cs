namespace ShopInventory.Web.Features.Revmax.Commands.FiscalizeCrossDeviceCreditNote;

public sealed class FiscalizeCrossDeviceCreditNoteResult
{
    public string CreditNoteNumber { get; init; } = string.Empty;
    public string FiscalInvoiceNumber { get; init; } = string.Empty;
    public string OriginalInvoiceNumber { get; init; } = string.Empty;
    public RevmaxTransactExtResponse Response { get; init; } = null!;
}