namespace ShopInventory.Common.Cluster;

/// <summary>One node's registration, as the gate sees it.</summary>
/// <param name="NodeKey">Machine and content root together, so two slots on one box are two nodes.</param>
/// <param name="BuildTimestampUtc">When the build was published, or null for an unstamped build.</param>
/// <param name="LastSeenUtc">The node's last heartbeat.</param>
public readonly record struct ClusterNodeBuild(string NodeKey, DateTime? BuildTimestampUtc, DateTime LastSeenUtc);

/// <summary>Whether this node may run clustered jobs, and why not when it may not.</summary>
/// <remarks>A reference type so the registry can swap the whole verdict in one volatile write.</remarks>
public sealed record StaleBuildVerdict(bool IsStale, string? Reason)
{
    public static StaleBuildVerdict MayRun { get; } = new(false, null);
}

/// <summary>
/// Decides whether this node's build is older than one a live peer is running.
/// </summary>
/// <remarks>
/// <para>
/// Quartz hands a trigger to whichever clustered member takes it first, so a node nobody deploys to
/// keeps running its share of every job with the code it had when it was last updated. That is not
/// hypothetical: on 2026-09-15 a node left on a 2026-09-12 build settled 45 till sales with a payment
/// each, weeks after the code that does so was removed, because it was still a member of the cluster.
/// Deploying to a node is how it gets new code; being in the cluster is how it gets work. Nothing
/// tied the two together until this.
/// </para>
/// <para>
/// The rules are deliberately one-directional. A build with no stamp — anything built outside a
/// deploy, which is every developer build — neither blocks nor is blocked, because an unstamped build
/// cannot be placed in the order and refusing to run on the strength of a guess would take a whole
/// environment's jobs down. Only a strictly newer stamp on a peer that is currently heart-beating
/// stops this node. "Currently" matters for a rollback: once the newer node stops, its registration
/// goes stale and the older build is free to work again, so a rollback needs no flag flipped.
/// </para>
/// </remarks>
public static class StaleBuildDecision
{
    public static StaleBuildVerdict Evaluate(
        string myNodeKey,
        DateTime? myBuildTimestampUtc,
        IEnumerable<ClusterNodeBuild> nodes,
        DateTime utcNow,
        TimeSpan liveWindow)
    {
        if (myBuildTimestampUtc is not { } mine)
        {
            return StaleBuildVerdict.MayRun;
        }

        var oldestLiveHeartbeat = utcNow - liveWindow;

        var newerPeer = nodes
            .Where(node => !string.Equals(node.NodeKey, myNodeKey, StringComparison.OrdinalIgnoreCase))
            .Where(node => node.LastSeenUtc >= oldestLiveHeartbeat)
            .Where(node => node.BuildTimestampUtc is { } peer && peer > mine)
            .OrderByDescending(node => node.BuildTimestampUtc)
            .FirstOrDefault();

        if (newerPeer.NodeKey is null)
        {
            return StaleBuildVerdict.MayRun;
        }

        return new StaleBuildVerdict(
            true,
            $"This node runs the build published {mine:yyyy-MM-dd HH:mm:ss}Z, and {newerPeer.NodeKey} is running one "
            + $"published {newerPeer.BuildTimestampUtc:yyyy-MM-dd HH:mm:ss}Z. Jobs are left to the newer node until "
            + "this one is deployed to or stopped.");
    }
}
