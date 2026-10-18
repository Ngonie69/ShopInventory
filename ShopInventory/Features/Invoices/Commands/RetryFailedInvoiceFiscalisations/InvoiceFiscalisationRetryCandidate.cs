namespace ShopInventory.Features.Invoices.Commands.RetryFailedInvoiceFiscalisations;

/// <param name="Failures">The invoice's failed fiscal transaction rows so far, whoever recorded them.</param>
public sealed record InvoiceFiscalisationRetryCandidate(int DocNum, int Failures, DateTime FirstFailedAtUtc);
