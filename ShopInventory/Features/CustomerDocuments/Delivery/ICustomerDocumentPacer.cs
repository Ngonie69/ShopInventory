namespace ShopInventory.Features.CustomerDocuments.Delivery;

/// <summary>
/// The clock and the waits the delivery job keeps time by, behind one seam so a test can run a whole
/// pass without waiting for it.
/// </summary>
public interface ICustomerDocumentPacer
{
    DateTime UtcNow { get; }

    /// <summary>The random part of the gap between sends, in whole seconds from 0 to <paramref name="maxSeconds"/>.</summary>
    int JitterSeconds(int maxSeconds);

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
