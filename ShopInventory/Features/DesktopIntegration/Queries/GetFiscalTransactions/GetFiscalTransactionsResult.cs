namespace ShopInventory.Features.DesktopIntegration.Queries.GetFiscalTransactions;

public sealed class GetFiscalTransactionsResult
{
    public FiscalTransactionLogSummary Summary { get; init; } = new();
    public List<FiscalTransactionLogItemDto> Transactions { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public bool HasMore { get; init; }
}