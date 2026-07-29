using ShopInventory.Features.Invoices.Events;

namespace ShopInventory.Services;

public interface IInvoiceFiscalizationQueue
{
    bool TryQueue(InvoiceFiscalizationWorkItem workItem);

    ValueTask<InvoiceFiscalizationWorkItem> DequeueAsync(CancellationToken cancellationToken = default);
}