namespace ShopInventory.Features.Invoices.Commands.RetryFailedInvoiceFiscalisations;

/// <param name="DocNum">The invoice's SAP document number.</param>
/// <param name="Failures">The invoice's failed fiscal transaction rows so far, whoever recorded them.</param>
/// <param name="FirstFailedAtUtc">When the invoice's earliest failed fiscal transaction was recorded, in UTC; the retry gives up on it after a cut-off from here.</param>
public sealed record InvoiceFiscalisationRetryCandidate(int DocNum, int Failures, DateTime FirstFailedAtUtc);
