using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Cluster;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The registry end of the build gate: what it writes about this node, and what it decides after
/// reading the rest of the cluster back.
/// </summary>
public sealed class ClusterBuildRegistryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public ClusterBuildRegistryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private ClusterBuildRegistry NewRegistry(DateTime? buildTimestampUtc = null) =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(),
            new BuildStampProvider(buildTimestampUtc),
            NullLogger<ClusterBuildRegistry>.Instance);

    private async Task<ClusterNodeEntity?> OwnRowAsync(ClusterBuildRegistry registry)
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .ClusterNodes.AsNoTracking().FirstOrDefaultAsync(node => node.NodeKey == registry.NodeKey);
    }

    private async Task AddPeerAsync(string nodeKey, DateTime? build, DateTime lastSeenUtc)
    {
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        context.ClusterNodes.Add(new ClusterNodeEntity
        {
            NodeKey = nodeKey,
            MachineName = nodeKey,
            ContentRoot = "C:/inetpub/api",
            BuildTimestampUtc = build,
            StartedAtUtc = lastSeenUtc,
            LastSeenUtc = lastSeenUtc
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task It_registers_this_node_and_lets_it_work()
    {
        var registry = NewRegistry();

        await registry.RefreshAsync(CancellationToken.None);

        Assert.False(registry.JobsAreVetoed);

        var row = await OwnRowAsync(registry);
        Assert.NotNull(row);
        Assert.Equal(Environment.MachineName, row!.MachineName);
        Assert.False(row.JobsVetoed);
    }

    [Fact]
    public async Task A_second_refresh_updates_the_same_row_rather_than_adding_one()
    {
        var registry = NewRegistry();

        await registry.RefreshAsync(CancellationToken.None);
        await registry.RefreshAsync(CancellationToken.None);

        using var scope = _provider.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .ClusterNodes.CountAsync(node => node.NodeKey == registry.NodeKey);
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task A_live_peer_on_a_newer_build_does_not_block_an_unstamped_node()
    {
        // The test assembly is never published by a deploy, so this node is unstamped - the case that
        // must keep working, or every developer and every test environment loses its jobs.
        await AddPeerAsync("KFL-DNS2|C:/inetpub/api-green", DateTime.UtcNow.AddHours(1), DateTime.UtcNow);
        var registry = NewRegistry();

        await registry.RefreshAsync(CancellationToken.None);

        Assert.False(registry.JobsAreVetoed);
        Assert.Null(registry.VetoReason);
    }

    [Fact]
    public async Task A_shutdown_takes_the_node_out_of_the_clusters_view()
    {
        var registry = NewRegistry();
        await registry.RefreshAsync(CancellationToken.None);

        await registry.DeregisterAsync(CancellationToken.None);

        Assert.Null(await OwnRowAsync(registry));
    }

    [Fact]
    public async Task An_unreadable_table_leaves_the_node_working()
    {
        // A node that starts before its migration has run must not stop doing its work over it.
        using (var scope = _provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .Database.ExecuteSqlRawAsync("DROP TABLE ClusterNodes");
        }

        var registry = NewRegistry();
        await registry.RefreshAsync(CancellationToken.None);

        Assert.False(registry.JobsAreVetoed);
    }

    [Fact]
    public async Task A_node_on_an_older_build_than_a_live_peer_stops_taking_jobs()
    {
        // 2026-09-15 in one test: the node on the 12th kept working while the 15th's build was live.
        var peerBuild = new DateTime(2026, 9, 15, 13, 33, 0, DateTimeKind.Utc);
        await AddPeerAsync("KFL-DNS2|C:/inetpub/api-green", peerBuild, DateTime.UtcNow);

        var registry = NewRegistry(new DateTime(2026, 9, 12, 5, 32, 0, DateTimeKind.Utc));
        await registry.RefreshAsync(CancellationToken.None);

        Assert.True(registry.JobsAreVetoed);
        Assert.Contains("KFL-DNS2", registry.VetoReason);

        // Written down as well as held in memory, so the state is visible without reading logs.
        var row = await OwnRowAsync(registry);
        Assert.True(row!.JobsVetoed);
        Assert.Contains("2026-09-15", row.VetoReason);
    }

    [Fact]
    public async Task It_starts_taking_jobs_again_once_the_newer_node_stops()
    {
        var peerBuild = new DateTime(2026, 9, 15, 13, 33, 0, DateTimeKind.Utc);
        await AddPeerAsync("KFL-DNS2|C:/inetpub/api-green", peerBuild, DateTime.UtcNow);

        var registry = NewRegistry(new DateTime(2026, 9, 12, 5, 32, 0, DateTimeKind.Utc));
        await registry.RefreshAsync(CancellationToken.None);
        Assert.True(registry.JobsAreVetoed);

        // The newer node shut down cleanly and removed its row: a rollback needs nothing flipped.
        using (var scope = _provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            context.ClusterNodes.RemoveRange(
                context.ClusterNodes.Where(node => node.NodeKey == "KFL-DNS2|C:/inetpub/api-green"));
            await context.SaveChangesAsync();
        }

        await registry.RefreshAsync(CancellationToken.None);

        Assert.False(registry.JobsAreVetoed);
        Assert.Null((await OwnRowAsync(registry))!.VetoReason);
    }
}
