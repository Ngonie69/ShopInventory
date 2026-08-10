using System.Collections.Concurrent;
using System.Threading.Channels;
using ShopInventory.DTOs;

namespace ShopInventory.Services;

/// <inheritdoc />
public sealed class InvoiceFiscalStatusBackfillQueue : IInvoiceFiscalStatusBackfillQueue
{
    /// <summary>
    /// Bounded, unlike the fiscalisation queue, because this one is fed by browsing rather than by
    /// documents being posted: paging through invoices could otherwise offer it work faster than
    /// the platform can answer, without limit. Dropping the overflow is safe — the status stays unknown
    /// and the next view of those invoices queues them again.
    /// </summary>
    private const int Capacity = 5000;

    private readonly Channel<InvoiceDto> _queue = Channel.CreateBounded<InvoiceDto>(
        new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });

    // Keeps one page view from queueing what another already did, and keeps a user paging back and
    // forth from queueing the same invoice repeatedly while it waits.
    private readonly ConcurrentDictionary<int, byte> _inFlight = new();

    public bool TryQueue(InvoiceDto invoice)
    {
        if (invoice.DocNum <= 0 || !_inFlight.TryAdd(invoice.DocNum, 0))
        {
            return false;
        }

        if (_queue.Writer.TryWrite(invoice))
        {
            return true;
        }

        // Full: let it be offered again rather than leaving a document number reserved forever.
        _inFlight.TryRemove(invoice.DocNum, out _);
        return false;
    }

    public ValueTask<InvoiceDto> DequeueAsync(CancellationToken cancellationToken = default) =>
        _queue.Reader.ReadAsync(cancellationToken);

    public void Release(int docNum) => _inFlight.TryRemove(docNum, out _);
}
