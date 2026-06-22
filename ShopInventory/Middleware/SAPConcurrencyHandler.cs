using Microsoft.Extensions.Options;
using ShopInventory.Configuration;

namespace ShopInventory.Middleware;

/// <summary>
/// DelegatingHandler that limits the number of concurrent HTTP requests to the SAP Service Layer.
/// Prevents parallel operations (Task.WhenAll in validation/reports) from overwhelming SAP
/// with too many simultaneous requests on a single session.
/// </summary>
public sealed class SAPConcurrencyHandler : DelegatingHandler
{
    private static readonly object SemaphoreGate = new();
    private static SemaphoreSlim? _semaphore;
    private static int _semaphoreLimit;
    private readonly int _maxConcurrentRequests;

    public SAPConcurrencyHandler(IOptions<SAPSettings> settings)
    {
        _maxConcurrentRequests = Math.Max(1, settings.Value.MaxConcurrentRequests);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var semaphore = GetSemaphore(_maxConcurrentRequests);
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static SemaphoreSlim GetSemaphore(int maxConcurrentRequests)
    {
        lock (SemaphoreGate)
        {
            if (_semaphore is null || _semaphoreLimit != maxConcurrentRequests)
            {
                _semaphore = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
                _semaphoreLimit = maxConcurrentRequests;
            }

            return _semaphore;
        }
    }
}
