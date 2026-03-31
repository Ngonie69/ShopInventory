namespace ShopInventory.Middleware;

/// <summary>
/// DelegatingHandler that limits the number of concurrent HTTP requests to the SAP Service Layer.
/// Prevents parallel operations (Task.WhenAll in validation/reports) from overwhelming SAP
/// with too many simultaneous requests on a single session.
/// </summary>
public class SAPConcurrencyHandler : DelegatingHandler
{
    private static readonly SemaphoreSlim _semaphore = new(10, 10);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
