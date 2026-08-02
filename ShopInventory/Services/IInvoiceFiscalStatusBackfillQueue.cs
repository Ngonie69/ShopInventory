using ShopInventory.DTOs;

namespace ShopInventory.Services;

/// <summary>
/// Queue of invoices whose fiscal status is not yet known locally and has to be read back from
/// REVMax.
/// </summary>
/// <remarks>
/// This is a cache fill: <c>FiscalDocumentStatusProjector</c> reads the status a page displays out
/// of the local database, and this is what puts it there. It used to run inside
/// <c>GetPagedInvoicesQuery</c>, one REVMax round trip per invoice, in sequence — on 2026-08-02 a
/// single page of 100 invoices spent 152 seconds on 46 of them, which is 46 of the 63 lookups made
/// in that whole three-hour session. Nothing about the page needs it to have finished: an invoice
/// whose status has not been read back yet is genuinely unknown, and says so.
/// </remarks>
public interface IInvoiceFiscalStatusBackfillQueue
{
    /// <summary>
    /// Queues <paramref name="invoice"/> unless its document number is already waiting. Returns
    /// false when it was dropped, which is not an error — the next page view that still finds the
    /// status unknown will offer it again.
    /// </summary>
    bool TryQueue(InvoiceDto invoice);

    ValueTask<InvoiceDto> DequeueAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a document number as no longer in flight so it can be queued again later. Called by
    /// the consumer once the invoice has been processed, successfully or not.
    /// </summary>
    void Release(int docNum);
}
