namespace ShopInventory.Features.DesktopIntegration.Queries.GetFiscalTransactions;

public sealed class FiscalTransactionLogSummary
{
    public int TotalTransactions { get; init; }
    public int SuccessCount { get; init; }
    public int FiscalisedCount { get; init; }
    public int NotFiscalisedCount { get; init; }
    public int FailedCount { get; init; }
    public int UniqueOperators { get; init; }
    public DateTime? LatestTransactionAtUtc { get; init; }
}