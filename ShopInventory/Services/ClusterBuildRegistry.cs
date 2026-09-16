using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Cluster;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Publishes this node's build to the cluster, reads back what the others are running, and holds the
/// answer to "may this node run clustered jobs".
/// </summary>
/// <remarks>
/// <para>
/// The verdict is cached rather than read per job fire: the fastest trigger runs every five seconds and
/// the answer changes only when a node starts or stops. <see cref="RefreshAsync"/> is called once before
/// the scheduler starts and then on the heartbeat, so a node that boots into a cluster running newer
/// code never takes a job at all.
/// </para>
/// <para>
/// Every failure here leaves the node running jobs. The gate exists to stop a stale build, not to stop
/// the cluster: if this table cannot be read — it does not exist yet on a node that starts before its
/// migration, the database is briefly unreachable — the safe answer is the one the cluster had before
/// the gate existed.
/// </para>
/// </remarks>
public sealed class ClusterBuildRegistry
{
    /// <summary>A registration older than this belongs to a node that is gone, so it cannot block anyone.</summary>
    public static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BuildStampProvider _build;
    private readonly ILogger<ClusterBuildRegistry> _logger;
    private readonly string _nodeKey;
    private readonly string _machineName;
    private readonly string _contentRoot;
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;

    private volatile StaleBuildVerdict _verdict = StaleBuildVerdict.MayRun;

    public ClusterBuildRegistry(
        IServiceScopeFactory scopeFactory,
        BuildStampProvider build,
        ILogger<ClusterBuildRegistry> logger)
    {
        _scopeFactory = scopeFactory;
        _build = build;
        _logger = logger;
        _machineName = Environment.MachineName;
        _contentRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _nodeKey = Truncate($"{_machineName}|{_contentRoot}", 500)!;
    }

    public string NodeKey => _nodeKey;

    /// <summary>What this node reports as its build, for logging.</summary>
    public string BuildDescription => _build.Description;

    /// <summary>True when a live peer runs a newer build, so this node must leave jobs alone.</summary>
    public bool JobsAreVetoed => _verdict.IsStale;

    public string? VetoReason => _verdict.Reason;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var utcNow = DateTime.UtcNow;
            var nodes = await context.ClusterNodes.AsNoTracking().ToListAsync(cancellationToken);

            var verdict = StaleBuildDecision.Evaluate(
                _nodeKey,
                _build.TimestampUtc,
                nodes.Select(node => new ClusterNodeBuild(node.NodeKey, node.BuildTimestampUtc, node.LastSeenUtc)),
                utcNow,
                LiveWindow);

            var was = _verdict;
            _verdict = verdict;

            await WriteOwnRowAsync(context, utcNow, verdict, cancellationToken);

            if (verdict.IsStale && !was.IsStale)
            {
                _logger.LogWarning(
                    "This node is not running background jobs. {Reason}", verdict.Reason);
            }
            else if (!verdict.IsStale && was.IsStale)
            {
                _logger.LogInformation(
                    "This node is running background jobs again: no live node holds a newer build than {Build}.",
                    _build.Description);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately not changing the verdict: an unreadable table must not decide anything.
            _logger.LogWarning(
                ex, "Could not compare this node's build against the cluster. Jobs continue as before.");
        }
    }

    /// <summary>
    /// Takes this node out of the cluster's view on a clean shutdown, so the node it was holding back
    /// starts working immediately rather than after the heartbeat window.
    /// </summary>
    public async Task DeregisterAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            await context.ClusterNodes
                .Where(node => node.NodeKey == _nodeKey)
                .ExecuteDeleteAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not remove this node's cluster registration on shutdown.");
        }
    }

    private async Task WriteOwnRowAsync(
        ApplicationDbContext context,
        DateTime utcNow,
        StaleBuildVerdict verdict,
        CancellationToken cancellationToken)
    {
        var row = await context.ClusterNodes.FirstOrDefaultAsync(node => node.NodeKey == _nodeKey, cancellationToken);

        if (row is null)
        {
            context.ClusterNodes.Add(new ClusterNodeEntity
            {
                NodeKey = _nodeKey,
                MachineName = Truncate(_machineName, 200)!,
                ContentRoot = Truncate(_contentRoot, 400)!,
                BuildTimestampUtc = _build.TimestampUtc,
                StartedAtUtc = _startedAtUtc,
                LastSeenUtc = utcNow,
                JobsVetoed = verdict.IsStale,
                VetoReason = Truncate(verdict.Reason, 1000)
            });
        }
        else
        {
            row.MachineName = Truncate(_machineName, 200)!;
            row.ContentRoot = Truncate(_contentRoot, 400)!;
            row.BuildTimestampUtc = _build.TimestampUtc;
            row.StartedAtUtc = _startedAtUtc;
            row.LastSeenUtc = utcNow;
            row.JobsVetoed = verdict.IsStale;
            row.VetoReason = Truncate(verdict.Reason, 1000);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Two processes on one machine and path is the only way to collide here, and the loser's
            // next heartbeat updates the row the winner wrote.
            _logger.LogDebug(ex, "Another process wrote this node's cluster registration first.");
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
