using ShopInventory.Common.Cluster;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the gate that keeps a node running old code from taking clustered work. On 2026-09-15 a node
/// left on a 2026-09-12 build settled 45 till sales with a payment each, weeks after that code was
/// removed, because deploying to a node and it being in the job cluster were unrelated facts.
/// </summary>
public sealed class StaleBuildDecisionTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private static ClusterNodeBuild Node(string key, DateTime? build, TimeSpan? seenAgo = null) =>
        new(key, build, Now - (seenAgo ?? TimeSpan.Zero));

    [Fact]
    public void A_node_holding_an_older_build_than_a_live_peer_is_stale()
    {
        var verdict = StaleBuildDecision.Evaluate(
            "DEV-TEST|C:/inetpub/api",
            new DateTime(2026, 9, 12, 5, 32, 0, DateTimeKind.Utc),
            [Node("KFL-DNS2|C:/inetpub/api-green", new DateTime(2026, 9, 15, 13, 33, 0, DateTimeKind.Utc))],
            Now,
            Window);

        Assert.True(verdict.IsStale);
        Assert.Contains("2026-09-15", verdict.Reason);
        Assert.Contains("KFL-DNS2", verdict.Reason);
    }

    [Fact]
    public void The_newest_node_runs_its_jobs()
    {
        var verdict = StaleBuildDecision.Evaluate(
            "KFL-DNS2|C:/inetpub/api-green",
            new DateTime(2026, 9, 15, 13, 33, 0, DateTimeKind.Utc),
            [Node("DEV-TEST|C:/inetpub/api", new DateTime(2026, 9, 12, 5, 32, 0, DateTimeKind.Utc))],
            Now,
            Window);

        Assert.False(verdict.IsStale);
        Assert.Null(verdict.Reason);
    }

    [Fact]
    public void Its_own_row_never_blocks_it()
    {
        // The node reads the table it just wrote to, so it must not compare itself against itself.
        var mine = new DateTime(2026, 9, 15, 13, 33, 0, DateTimeKind.Utc);

        var verdict = StaleBuildDecision.Evaluate(
            "KFL-DNS2|C:/inetpub/api-green",
            mine,
            [Node("kfl-dns2|c:/inetpub/api-green", mine.AddHours(2))],
            Now,
            Window);

        Assert.False(verdict.IsStale);
    }

    [Fact]
    public void A_newer_node_that_has_stopped_heart_beating_stops_blocking()
    {
        // This is the rollback: put an older build back and it works once the newer node is gone,
        // with no flag to remember to flip.
        var verdict = StaleBuildDecision.Evaluate(
            "KFL-DNS2|C:/inetpub/api-blue",
            new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc),
            [Node("KFL-DNS2|C:/inetpub/api-green", new DateTime(2026, 9, 15, 13, 33, 0, DateTimeKind.Utc), seenAgo: TimeSpan.FromMinutes(6))],
            Now,
            Window);

        Assert.False(verdict.IsStale);
    }

    [Fact]
    public void An_unstamped_build_is_never_blocked()
    {
        // Every developer build is unstamped. Refusing to run jobs on a guess would take a whole
        // environment's background work down.
        var verdict = StaleBuildDecision.Evaluate(
            "LAPTOP|C:/src/api",
            myBuildTimestampUtc: null,
            [Node("KFL-DNS2|C:/inetpub/api-green", new DateTime(2026, 9, 15, 13, 33, 0, DateTimeKind.Utc))],
            Now,
            Window);

        Assert.False(verdict.IsStale);
    }

    [Fact]
    public void An_unstamped_peer_blocks_nobody()
    {
        var verdict = StaleBuildDecision.Evaluate(
            "KFL-DNS2|C:/inetpub/api-green",
            new DateTime(2026, 9, 15, 13, 33, 0, DateTimeKind.Utc),
            [Node("LAPTOP|C:/src/api", null)],
            Now,
            Window);

        Assert.False(verdict.IsStale);
    }

    [Fact]
    public void An_identical_build_on_both_slots_runs_on_both()
    {
        // The normal state during a blue/green overlap: same build, both members working.
        var build = new DateTime(2026, 9, 15, 13, 33, 0, DateTimeKind.Utc);

        var verdict = StaleBuildDecision.Evaluate(
            "KFL-DNS2|C:/inetpub/api-blue",
            build,
            [Node("KFL-DNS2|C:/inetpub/api-green", build)],
            Now,
            Window);

        Assert.False(verdict.IsStale);
    }

    [Fact]
    public void The_reason_names_the_newest_peer_when_several_are_live()
    {
        var verdict = StaleBuildDecision.Evaluate(
            "DEV-TEST|C:/inetpub/api",
            new DateTime(2026, 9, 12, 5, 32, 0, DateTimeKind.Utc),
            [
                Node("KFL-DNS2|C:/inetpub/api-blue", new DateTime(2026, 9, 14, 13, 33, 0, DateTimeKind.Utc)),
                Node("KFL-DNS2|C:/inetpub/api-green", new DateTime(2026, 9, 16, 1, 21, 0, DateTimeKind.Utc))
            ],
            Now,
            Window);

        Assert.True(verdict.IsStale);
        Assert.Contains("api-green", verdict.Reason);
        Assert.Contains("2026-09-16", verdict.Reason);
    }

    [Fact]
    public void The_deploys_own_timestamp_format_reads_back()
    {
        // Produced by (Get-Date).ToUniversalTime().ToString('o') in Update-Production.ps1 - the exact
        // string the publish passes as -p:BuildTimestampUtc. If this ever stops parsing, every node
        // silently becomes unstamped and the gate quietly stops guarding anything.
        var parsed = BuildStamp.Parse("2026-09-16T00:14:31.6844357Z");

        Assert.NotNull(parsed);
        Assert.Equal(DateTimeKind.Utc, parsed!.Value.Kind);
        Assert.Equal(new DateTime(2026, 9, 16, 0, 14, 31, DateTimeKind.Utc), parsed.Value.AddTicks(-parsed.Value.Ticks % TimeSpan.TicksPerSecond));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    public void An_unreadable_stamp_counts_as_no_stamp(string? raw)
    {
        Assert.Null(BuildStamp.Parse(raw));
    }

    [Fact]
    public void A_stamp_with_an_offset_is_read_as_the_instant_it_names()
    {
        // 02:14 in Harare is 00:14 UTC. Reading it as local time would move every comparison by the
        // reading machine's offset.
        var parsed = BuildStamp.Parse("2026-09-16T02:14:31.0000000+02:00");

        Assert.Equal(new DateTime(2026, 9, 16, 0, 14, 31, DateTimeKind.Utc), parsed);
        Assert.Equal(DateTimeKind.Utc, parsed!.Value.Kind);
    }

    [Fact]
    public void A_stamp_with_no_zone_is_read_as_utc()
    {
        var parsed = BuildStamp.Parse("2026-09-16T00:14:31");

        Assert.Equal(new DateTime(2026, 9, 16, 0, 14, 31, DateTimeKind.Utc), parsed);
    }
}
