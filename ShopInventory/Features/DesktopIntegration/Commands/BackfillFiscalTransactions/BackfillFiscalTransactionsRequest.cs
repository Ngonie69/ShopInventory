namespace ShopInventory.Features.DesktopIntegration.Commands.BackfillFiscalTransactions;

public sealed class BackfillFiscalTransactionsRequest
{
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public int MaxInvoices { get; init; } = 500;
    public int PageSize { get; init; } = 100;
}