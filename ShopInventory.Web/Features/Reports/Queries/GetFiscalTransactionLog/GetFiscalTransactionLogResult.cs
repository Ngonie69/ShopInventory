namespace ShopInventory.Web.Features.Reports.Queries.GetFiscalTransactionLog;

public sealed class GetFiscalTransactionLogResult
{
    public FiscalTransactionLogSummaryModel Summary { get; init; } = new();
    public List<FiscalTransactionLogItemModel> Transactions { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public bool HasMore { get; init; }
}