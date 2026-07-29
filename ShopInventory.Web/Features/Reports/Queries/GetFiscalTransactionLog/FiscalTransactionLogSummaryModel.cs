namespace ShopInventory.Web.Features.Reports.Queries.GetFiscalTransactionLog;

public sealed class FiscalTransactionLogSummaryModel
{
    public int TotalTransactions { get; init; }
    public int SuccessCount { get; init; }
    public int FiscalisedCount { get; init; }
    public int NotFiscalisedCount { get; init; }
    public int FailedCount { get; init; }
    public int UniqueOperators { get; init; }
    public DateTime? LatestTransactionAtUtc { get; init; }
}