namespace ShopInventory.Features.FiscalisationConfiguration.Commands.FlagRepostedFiscalTransactions;

/// <param name="Ran">False when the pass is switched off: SAP disabled, no marker, or no start date.</param>
/// <param name="InvoicesChecked">Distinct invoice numbers whose remarks were read from SAP.</param>
/// <param name="RowsFlagged">Log rows newly marked as reposted.</param>
public sealed record FlagRepostedFiscalTransactionsResult(
    bool Ran,
    int InvoicesChecked,
    int RowsFlagged);
