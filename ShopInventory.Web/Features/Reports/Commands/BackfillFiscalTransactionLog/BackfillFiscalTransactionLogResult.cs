namespace ShopInventory.Web.Features.Reports.Commands.BackfillFiscalTransactionLog;

public sealed class BackfillFiscalTransactionLogResult
{
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }
    public int AvailableInvoiceCount { get; init; }
    public int ScannedInvoiceCount { get; init; }
    public int AlreadyTrackedCount { get; init; }
    public int FiscalisedFoundCount { get; init; }
    public int TransactionsSyncedCount { get; init; }
    public int NotFiscalisedCount { get; init; }
    public int LookupFailedCount { get; init; }
    public int SyncFailedCount { get; init; }
}