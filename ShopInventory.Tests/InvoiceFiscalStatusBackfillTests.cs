using ShopInventory.Common.Fiscalization;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the hand-off that keeps fiscal status lookups out of the invoice list request.
/// </summary>
/// <remarks>
/// The list handler used to do the lookups itself, in sequence, up to 100 per page. Each takes
/// about three seconds, so on 2026-08-02 a single page of 100 invoices spent 152
/// seconds on 46 of them — 46 of the 63 lookups made in that entire three-hour session. The page
/// never needed them: the status it renders comes from the local projection, and an invoice that
/// has not been read back yet is honestly Unknown.
/// </remarks>
public class InvoiceFiscalStatusBackfillTests
{
    private static InvoiceDto Invoice(int docNum, string status = "Unknown") =>
        new() { DocNum = docNum, FiscalizationStatus = status };

    // A dequeue that blocks means the queue is broken; fail the test rather than the run.
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;

    [Fact]
    public void Only_invoices_with_an_unknown_status_are_queued()
    {
        var queue = new InvoiceFiscalStatusBackfillQueue();

        var queued = InvoiceFiscalTransactionSync.QueueUnknownInvoicesForBackfill(
            [
                Invoice(1, "Unknown"),
                Invoice(2, "Fiscalised"),
                Invoice(3, "Not Fiscalised"),
                Invoice(4, "Unknown")
            ],
            queue);

        Assert.Equal(2, queued);
    }

    [Fact]
    public void A_page_that_repeats_an_invoice_queues_it_once()
    {
        var queue = new InvoiceFiscalStatusBackfillQueue();

        var queued = InvoiceFiscalTransactionSync.QueueUnknownInvoicesForBackfill(
            [Invoice(700), Invoice(700), Invoice(701)],
            queue);

        Assert.Equal(2, queued);
    }

    [Fact]
    public void An_invoice_already_waiting_is_not_queued_again_by_the_next_page_view()
    {
        var queue = new InvoiceFiscalStatusBackfillQueue();

        Assert.Equal(1, InvoiceFiscalTransactionSync.QueueUnknownInvoicesForBackfill([Invoice(700)], queue));
        Assert.Equal(0, InvoiceFiscalTransactionSync.QueueUnknownInvoicesForBackfill([Invoice(700)], queue));
    }

    /// <summary>
    /// Without the release the in-flight set would only ever grow, and an invoice whose lookup
    /// failed could never be retried.
    /// </summary>
    [Fact]
    public async Task An_invoice_can_be_queued_again_once_the_consumer_releases_it()
    {
        var queue = new InvoiceFiscalStatusBackfillQueue();

        Assert.True(queue.TryQueue(Invoice(700)));
        var dequeued = await queue.DequeueAsync(Timeout);
        queue.Release(dequeued.DocNum);

        Assert.True(queue.TryQueue(Invoice(700)));
    }

    [Fact]
    public async Task Queued_invoices_come_back_out_in_order()
    {
        var queue = new InvoiceFiscalStatusBackfillQueue();
        InvoiceFiscalTransactionSync.QueueUnknownInvoicesForBackfill(
            [Invoice(10), Invoice(11), Invoice(12)],
            queue);

        Assert.Equal(10, (await queue.DequeueAsync(Timeout)).DocNum);
        Assert.Equal(11, (await queue.DequeueAsync(Timeout)).DocNum);
        Assert.Equal(12, (await queue.DequeueAsync(Timeout)).DocNum);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_invoice_without_a_document_number_cannot_be_looked_up(int docNum)
    {
        var queue = new InvoiceFiscalStatusBackfillQueue();

        Assert.False(queue.TryQueue(Invoice(docNum)));
        Assert.Equal(0, InvoiceFiscalTransactionSync.QueueUnknownInvoicesForBackfill([Invoice(docNum)], queue));
    }

    [Fact]
    public void Queueing_a_null_page_is_a_no_op()
    {
        Assert.Equal(0, InvoiceFiscalTransactionSync.QueueUnknownInvoicesForBackfill(null, new InvoiceFiscalStatusBackfillQueue()));
    }
}
