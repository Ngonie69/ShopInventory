namespace ShopInventory.Features.Invoices.Commands.CancelInvoice;

/// <param name="InvoiceDocEntry">The invoice that was cancelled.</param>
/// <param name="InvoiceDocNum">Its SAP document number — what a person calls it.</param>
/// <param name="CreditNoteId">The local credit note record.</param>
/// <param name="CreditNoteNumber">The credit note's own reference.</param>
/// <param name="CreditNoteDocEntry">Its SAP document entry, once posted.</param>
/// <param name="CreditNoteDocNum">Its SAP document number, once posted.</param>
/// <param name="CreditedAmount">What the credit note reverses.</param>
/// <param name="Currency">The currency both documents are in.</param>
/// <param name="Reason">The reason stored on every credit note line.</param>
/// <param name="NotifiedWarehouses">
/// The tills told about the cancellation. Empty means the invoice could not be traced to a till —
/// the credit note still exists, and this says plainly that nobody was pushed the news.
/// </param>
public sealed record CancelInvoiceResult(
    int InvoiceDocEntry,
    int InvoiceDocNum,
    int CreditNoteId,
    string CreditNoteNumber,
    int? CreditNoteDocEntry,
    int? CreditNoteDocNum,
    decimal CreditedAmount,
    string? Currency,
    string Reason,
    IReadOnlyList<string> NotifiedWarehouses);
