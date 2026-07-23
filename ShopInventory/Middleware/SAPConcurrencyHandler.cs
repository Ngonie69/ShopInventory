using System.Diagnostics;
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
    private readonly ILogger<SAPConcurrencyHandler> _logger;

    public SAPConcurrencyHandler(
        IOptions<SAPSettings> settings,
        ILogger<SAPConcurrencyHandler> logger)
    {
        _maxConcurrentRequests = Math.Max(1, settings.Value.MaxConcurrentRequests);
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var semaphore = GetSemaphore(_maxConcurrentRequests);
        var availableSlots = semaphore.CurrentCount;
        var waitStarted = Stopwatch.GetTimestamp();
        await semaphore.WaitAsync(cancellationToken);
        var waitDuration = Stopwatch.GetElapsedTime(waitStarted);

        if (waitDuration >= TimeSpan.FromSeconds(2))
        {
            _logger.LogWarning(
                "SAP request waited {WaitMilliseconds}ms for a concurrency slot ({Method} {Path}); available slots at arrival {AvailableSlots}/{MaxConcurrentRequests}",
                waitDuration.TotalMilliseconds,
                request.Method,
                request.RequestUri?.AbsolutePath,
                availableSlots,
                _maxConcurrentRequests);
        }
        else if (waitDuration >= TimeSpan.FromMilliseconds(100))
        {
            _logger.LogDebug(
                "SAP request waited {WaitMilliseconds}ms for a concurrency slot ({Method} {Path})",
                waitDuration.TotalMilliseconds,
                request.Method,
                request.RequestUri?.AbsolutePath);
        }

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
