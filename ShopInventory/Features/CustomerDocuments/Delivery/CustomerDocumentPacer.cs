namespace ShopInventory.Features.CustomerDocuments.Delivery;

/// <summary>The real clock.</summary>
public sealed class CustomerDocumentPacer : ICustomerDocumentPacer
{
    public DateTime UtcNow => DateTime.UtcNow;

    public int JitterSeconds(int maxSeconds) => maxSeconds <= 0 ? 0 : Random.Shared.Next(0, maxSeconds + 1);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
}
