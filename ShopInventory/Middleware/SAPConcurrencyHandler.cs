using System.Diagnostics;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;

namespace ShopInventory.Middleware;

/// <summary>
/// DelegatingHandler that limits the number of concurrent HTTP requests to the SAP Service Layer.
/// Prevents parallel operations (Task.WhenAll in validation/reports) from overwhelming SAP
/// with too many simultaneous requests on a single session.
/// </summary>
/// <remarks>
/// The limit is split rather than flat. Every slot is reachable by a request somebody is waiting
/// on (see <see cref="SapRequestPriority"/>), but background work — POD validation, invoice lists,
/// catalog sync — is capped below the total. Without that cap the limiter is strictly
/// first-come-first-served, so an approval's twenty-odd sequential round-trips each queued behind
/// whatever bulk traffic happened to arrive first, turning a seconds-long post into minutes.
/// Because background callers can never hold every slot, an interactive request waits at most for
/// one in-flight background request to finish.
/// </remarks>
public sealed class SAPConcurrencyHandler : DelegatingHandler
{
    private static readonly object SemaphoreGate = new();
    private static SemaphoreSlim? _allRequests;
    private static SemaphoreSlim? _backgroundRequests;
    private static int _semaphoreLimit;
    private static int _backgroundLimit;
    private readonly int _maxConcurrentRequests;
    private readonly int _maxBackgroundRequests;
    private readonly ILogger<SAPConcurrencyHandler> _logger;

    public SAPConcurrencyHandler(
        IOptions<SAPSettings> settings,
        ILogger<SAPConcurrencyHandler> logger)
    {
        _maxConcurrentRequests = Math.Max(1, settings.Value.MaxConcurrentRequests);

        // At least one slot stays out of reach of background work, and background work keeps at
        // least one of its own so a stream of approvals cannot starve the queue consumers.
        var reserved = Math.Clamp(settings.Value.InteractiveReservedRequests, 0, _maxConcurrentRequests - 1);
        _maxBackgroundRequests = Math.Max(1, _maxConcurrentRequests - reserved);
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var (allRequests, backgroundRequests) = GetSemaphores(_maxConcurrentRequests, _maxBackgroundRequests);
        var isInteractive = SapRequestPriority.IsInteractive;
        var availableSlots = allRequests.CurrentCount;
        var waitStarted = Stopwatch.GetTimestamp();

        // Background callers take the background permit first, which is what holds them below the
        // total; interactive callers go straight for a slot.
        if (!isInteractive)
        {
            await backgroundRequests.WaitAsync(cancellationToken);
        }

        try
        {
            await allRequests.WaitAsync(cancellationToken);
        }
        catch
        {
            if (!isInteractive)
            {
                backgroundRequests.Release();
            }

            throw;
        }

        var waitDuration = Stopwatch.GetElapsedTime(waitStarted);

        if (waitDuration >= TimeSpan.FromSeconds(2))
        {
            _logger.LogWarning(
                "SAP request waited {WaitMilliseconds}ms for a concurrency slot ({Method} {Path}, {Priority}); available slots at arrival {AvailableSlots}/{MaxConcurrentRequests}",
                waitDuration.TotalMilliseconds,
                request.Method,
                request.RequestUri?.AbsolutePath,
                isInteractive ? "interactive" : "background",
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
            allRequests.Release();
            if (!isInteractive)
            {
                backgroundRequests.Release();
            }
        }
    }

    private static (SemaphoreSlim AllRequests, SemaphoreSlim BackgroundRequests) GetSemaphores(
        int maxConcurrentRequests,
        int maxBackgroundRequests)
    {
        lock (SemaphoreGate)
        {
            if (_allRequests is null
                || _backgroundRequests is null
                || _semaphoreLimit != maxConcurrentRequests
                || _backgroundLimit != maxBackgroundRequests)
            {
                _allRequests = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
                _backgroundRequests = new SemaphoreSlim(maxBackgroundRequests, maxBackgroundRequests);
                _semaphoreLimit = maxConcurrentRequests;
                _backgroundLimit = maxBackgroundRequests;
            }

            return (_allRequests, _backgroundRequests);
        }
    }
}
