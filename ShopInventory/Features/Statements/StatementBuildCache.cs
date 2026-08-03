using System.Diagnostics;
using ErrorOr;
using Microsoft.Extensions.Caching.Memory;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Statements;

/// <summary>
/// Builds a customer statement at most once per key, keeps a successful result for a short window,
/// and runs the build detached from whichever caller triggered it.
/// </summary>
public interface IStatementBuildCache
{
    Task<ErrorOr<CustomerStatementResponseDto>> GetOrBuildAsync(
        string key,
        Func<CancellationToken, Task<ErrorOr<CustomerStatementResponseDto>>> build,
        CancellationToken callerCancellation);
}

/// <summary>
/// The reason a statement could never be produced once it grew past the portal's five-minute HTTP
/// timeout, and the fix for it.
/// </summary>
/// <remarks>
/// A controller's <c>CancellationToken</c> parameter is bound to <c>HttpContext.RequestAborted</c>,
/// and the statement handler threaded it all the way down to <c>HttpClient.SendAsync</c> against
/// SAP. So when the web app's client gave up at 300 seconds and dropped the connection, Kestrel
/// aborted the request and every SAP round trip already paid for was thrown away. Nothing was kept,
/// so the customer's retry started from zero and took exactly as long — which is why the failure
/// was permanent rather than intermittent. No number of attempts could produce a statement that
/// takes longer than one attempt is allowed to last.
///
/// Two properties fix that, and they only work together:
///
/// The build runs on its own cancellation token, so a caller walking away no longer destroys it.
/// It finishes and caches, and the retry is then a cache hit rather than a fresh five minutes.
///
/// A build already running is joined rather than duplicated. Without that, an impatient customer
/// clicking Generate three times would put three identical SAP fan-outs through six shared
/// concurrency slots, each making the others slower.
///
/// Only successes are cached. A statement for a customer who does not exist yet, or one that failed
/// against a momentarily unavailable SAP, must not be the answer for the next five minutes — but it
/// is still worth single-flighting, so a retry storm against a sick SAP does not multiply itself.
/// </remarks>
public sealed class StatementBuildCache(
    IMemoryCache memoryCache,
    ILogger<StatementBuildCache> logger) : IStatementBuildCache
{
    /// <summary>
    /// How long a built statement is served without going back to SAP.
    /// </summary>
    /// <remarks>
    /// Long enough that the retry after a client timeout lands on it — that is the whole point —
    /// and short enough that a payment posted this morning appears on a statement run this
    /// afternoon. The reports feature settled on three minutes for the same trade-off.
    /// </remarks>
    internal static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The ceiling on a single build.
    /// </summary>
    /// <remarks>
    /// Deliberately longer than the portal's 300-second HTTP timeout. A build that outlives the
    /// browser still has to be allowed to finish, because finishing is what puts it in the cache for
    /// the next attempt. Capping it at the client's timeout would reproduce the original bug with
    /// extra steps.
    /// </remarks>
    internal static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Prune only once the map is big enough to be worth walking.</summary>
    private const int PruneThreshold = 64;

    private readonly object _buildGate = new();
    private readonly Dictionary<string, Task<ErrorOr<CustomerStatementResponseDto>>> _inFlight =
        new(StringComparer.Ordinal);

    public async Task<ErrorOr<CustomerStatementResponseDto>> GetOrBuildAsync(
        string key,
        Func<CancellationToken, Task<ErrorOr<CustomerStatementResponseDto>>> build,
        CancellationToken callerCancellation)
    {
        if (memoryCache.TryGetValue(key, out CustomerStatementResponseDto? cached) && cached is not null)
        {
            logger.LogInformation("Serving statement {StatementKey} from cache", key);
            return cached;
        }

        var buildTask = GetOrStartBuild(key, build);

        // WaitAsync observes the caller's cancellation without touching the build behind it. A portal
        // user whose HttpClient gives up at 300 seconds stops waiting here; SAP keeps working, the
        // result still reaches the cache, and that same user's retry is answered from it.
        return await buildTask.WaitAsync(callerCancellation);
    }

    private Task<ErrorOr<CustomerStatementResponseDto>> GetOrStartBuild(
        string key,
        Func<CancellationToken, Task<ErrorOr<CustomerStatementResponseDto>>> build)
    {
        lock (_buildGate)
        {
            // A completed entry is never handed out: its result has already been cached or discarded,
            // and returning it here would outlive the cache's own expiry.
            if (_inFlight.TryGetValue(key, out var running) && !running.IsCompleted)
            {
                logger.LogInformation("Joining the statement build already running for {StatementKey}", key);
                return running;
            }

            PruneCompletedBuilds();

            var started = RunDetachedAsync(key, build);
            _inFlight[key] = started;
            return started;
        }
    }

    private Task<ErrorOr<CustomerStatementResponseDto>> RunDetachedAsync(
        string key,
        Func<CancellationToken, Task<ErrorOr<CustomerStatementResponseDto>>> build)
    {
        // Task.Run rather than a bare call so the build is not started on the request thread and
        // owes it nothing. It does inherit the ambient SAP priority — SapRequestPriority is an
        // AsyncLocal and the execution context is captured here — which is correct: somebody is
        // waiting on this statement even if the connection they were waiting on has since dropped.
        var task = Task.Run(async () =>
        {
            var started = Stopwatch.GetTimestamp();
            using var timeout = new CancellationTokenSource(BuildTimeout);

            var result = await build(timeout.Token);
            var elapsed = Stopwatch.GetElapsedTime(started);

            if (result.IsError)
            {
                logger.LogWarning(
                    "Statement {StatementKey} failed after {ElapsedMs:F0}ms: {Error}",
                    key,
                    elapsed.TotalMilliseconds,
                    result.FirstError.Description);
                return result;
            }

            memoryCache.Set(key, result.Value, ResultLifetime);
            logger.LogInformation(
                "Statement {StatementKey} built in {ElapsedMs:F0}ms and cached for {CacheMinutes} minutes",
                key,
                elapsed.TotalMilliseconds,
                ResultLifetime.TotalMinutes);

            return result;
        });

        // Nobody is guaranteed to await this — being abandoned is the case it exists for — so the
        // exception is observed here rather than surfacing as an unobserved task exception.
        _ = task.ContinueWith(
            faulted => logger.LogError(faulted.Exception, "Statement build {StatementKey} threw", key),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

        return task;
    }

    /// <summary>
    /// Drops finished entries. Called under <see cref="_buildGate"/> only when a new build starts,
    /// so the map cannot grow one entry per distinct customer and date range forever.
    /// </summary>
    private void PruneCompletedBuilds()
    {
        if (_inFlight.Count < PruneThreshold)
        {
            return;
        }

        foreach (var finished in _inFlight.Where(entry => entry.Value.IsCompleted).Select(entry => entry.Key).ToList())
        {
            _inFlight.Remove(finished);
        }
    }
}
