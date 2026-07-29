namespace ShopInventory.Features.DesktopIntegration.Commands.BackfillFiscalTransactions;

public sealed record BackfillFiscalTransactionsResult(
    DateTime FromUtc,
    DateTime ToUtc,
    int AvailableInvoiceCount,
    int ScannedInvoiceCount,
    int AlreadyTrackedCount,
    int FiscalisedFoundCount,
    int TransactionsSyncedCount,
    int NotFiscalisedCount,
    int LookupFailedCount,
    int SyncFailedCount);