using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Configuration;
using ShopInventory.Common.Caching;
using ShopInventory.Features.Invoices.Queries.GetPodUploadStatus;

namespace ShopInventory.Services;

/// <summary>
/// Quartz job that rebuilds the POD upload-status reports people are actively using, before their
/// cached snapshots go stale.
/// </summary>
/// <remarks>
/// A cache hit answers in 30–90 ms; a miss took between 6 and 64 seconds on 2026-08-20, and six of
/// them landed in ninety minutes because the snapshots live fifteen minutes and users pick their own
/// date ranges. Every one of those seconds was spent with somebody waiting.
/// <para>
/// This does not reduce the SAP work by much — it moves it off the request path. What keeps it from
/// being a net loss is that it only rebuilds shapes <see cref="PodReportWarmSet"/> has seen asked
/// for recently: nothing at all overnight, and during the day only the two or three in play, rather
/// than a fixed list of presets refreshed on a timer whether or not anyone opened them.
/// </para>
/// <para>
/// Scoped reports are warmed on the same terms as any other. The key carries the scope, so a
/// driver's snapshot is rebuilt under the assignment set it was built for and never crosses over.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class PodReportWarmJob : IJob
{
    /// <summary>
    /// How far ahead of expiry a snapshot is rebuilt. Comfortably wider than the job interval, so a
    /// snapshot cannot slip through the gap between two runs and expire unnoticed.
    /// </summary>
    private static readonly TimeSpan RefreshAhead = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A ceiling per run. Rebuilds are serial and each can take the best part of a minute, so this
    /// keeps one fire from running into the next; anything skipped is picked up on the next pass.
    /// </summary>
    private const int MaxRebuildsPerRun = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PodReportWarmSet _warmSet;
    private readonly PodReportCacheSettings _cacheSettings;
    private readonly ILogger<PodReportWarmJob> _logger;

    public PodReportWarmJob(
        IServiceScopeFactory scopeFactory,
        PodReportWarmSet warmSet,
        IOptions<PodReportCacheSettings> cacheSettings,
        ILogger<PodReportWarmJob> logger)
    {
        _scopeFactory = scopeFactory;
        _warmSet = warmSet;
        _cacheSettings = cacheSettings.Value;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var shapes = _warmSet.ActiveShapes();
        if (shapes.Count == 0)
        {
            return;
        }

        var cancellationToken = context.CancellationToken;
        var rebuilt = 0;

        foreach (var shape in shapes)
        {
            if (rebuilt >= MaxRebuildsPerRun || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            using var scope = _scopeFactory.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<IPodReportCacheStore>();

            if (!cache.Enabled)
            {
                return;
            }

            var snapshot = await cache.GetAsync(shape.FromDate, shape.ToDate, shape.ScopeKey, cancellationToken);
            if (IsWarmEnough(snapshot, TimeSpan.FromMinutes(_cacheSettings.FreshnessMinutes)))
            {
                continue;
            }

            try
            {
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                // UserId null keeps this on the report's own scoping rules rather than a user's:
                // a null user is not a driver, so the handler resolves the global scope and the
                // snapshot it saves is the one the global readers will hit.
                var result = await mediator.Send(
                    new GetPodUploadStatusQuery(shape.FromDate, shape.ToDate, UserId: null),
                    cancellationToken);

                rebuilt++;

                if (result.IsError)
                {
                    _logger.LogWarning(
                        "Could not warm the POD report for {FromDate:yyyy-MM-dd} to {ToDate:yyyy-MM-dd}: {Reason}",
                        shape.FromDate,
                        shape.ToDate,
                        string.Join("; ", result.Errors.Select(error => error.Description)));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A warm failure costs a person one slow report, not a broken one. Never fault the
                // trigger over it.
                _logger.LogWarning(
                    ex,
                    "Could not warm the POD report for {FromDate:yyyy-MM-dd} to {ToDate:yyyy-MM-dd}",
                    shape.FromDate,
                    shape.ToDate);
            }
        }

        if (rebuilt > 0)
        {
            _logger.LogInformation(
                "Warmed {RebuiltCount} of {ActiveCount} POD report(s) in active use",
                rebuilt,
                shapes.Count);
        }
    }

    /// <summary>
    /// Whether a snapshot will still be servable for long enough that this run can leave it alone.
    /// </summary>
    internal static bool IsWarmEnough(
        PodReportCacheSnapshot? snapshot,
        TimeSpan freshnessWindow,
        DateTime? nowUtc = null)
    {
        // Mirrors what the reader accepts: a snapshot with incomplete credit-note data is rebuilt on
        // read however fresh it is, so warming has to treat it as cold too.
        if (!GetPodUploadStatusHandler.CanServeCachedSnapshot(snapshot))
        {
            return false;
        }

        // The store reports freshness as a boolean but not the instant it lapses, so it is derived
        // from the refresh time and the configured window.
        var freshUntil = snapshot!.RefreshedAtUtc + freshnessWindow;
        return freshUntil - (nowUtc ?? DateTime.UtcNow) > RefreshAhead;
    }
}
