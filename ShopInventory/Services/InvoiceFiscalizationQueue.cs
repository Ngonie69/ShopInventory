using System.Threading.Channels;
using ShopInventory.Features.Invoices.Events;

namespace ShopInventory.Services;

public sealed class InvoiceFiscalizationQueue : IInvoiceFiscalizationQueue
{
    private readonly Channel<InvoiceFiscalizationWorkItem> _queue = Channel.CreateUnbounded<InvoiceFiscalizationWorkItem>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    public bool TryQueue(InvoiceFiscalizationWorkItem workItem) =>
        _queue.Writer.TryWrite(workItem);

    public ValueTask<InvoiceFiscalizationWorkItem> DequeueAsync(CancellationToken cancellationToken = default) =>
        _queue.Reader.ReadAsync(cancellationToken);
}