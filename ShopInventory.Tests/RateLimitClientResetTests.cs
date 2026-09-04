using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.RateLimit;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Letting a client through again, with <c>POST /api/RateLimit/reset/{clientId}</c> - the single
/// route for it since <c>unblock/{clientId}</c> was removed as a duplicate.
///
/// Reset used to zero the counter and leave <c>IsBlocked</c> where it was, so resetting a blocked
/// client left it blocked - and a client being blocked is the only state anybody reaches for reset
/// in. An operator clearing a client and watching it stay shut out has no way to tell a broken
/// endpoint from a client that is still hammering the API, which is how a five-second fix becomes an
/// escalation.
/// </summary>
public sealed class RateLimitClientResetTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public RateLimitClientResetTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using (var seed = NewContext())
        {
            seed.Database.EnsureCreated();
        }

        _provider = new ServiceCollection()
            .AddScoped<ApplicationDbContext>(_ => NewContext())
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Clearing_a_blocked_client_lets_it_through_again()
    {
        await GivenClientAsync("ip:10.10.10.6", blocked: true, requestCount: 500);

        var cleared = await ResetAsync("ip:10.10.10.6");

        Assert.True(cleared);
        var limit = await ReadAsync("ip:10.10.10.6");
        Assert.False(limit.IsBlocked);
        Assert.Null(limit.BlockExpiresAt);
        Assert.Equal(0, limit.RequestCount);
    }

    [Fact]
    public async Task Clearing_restarts_the_window()
    {
        // Zeroing the counter without moving the window start would leave the client with a full
        // allowance inside a window that is about to roll anyway - two allowances back to back.
        var longAgo = DateTime.UtcNow.AddHours(-3);
        await GivenClientAsync("ip:10.0.0.9", blocked: false, requestCount: 99, windowStart: longAgo);

        await ResetAsync("ip:10.0.0.9");

        Assert.True((await ReadAsync("ip:10.0.0.9")).WindowStart > longAgo);
    }

    [Fact]
    public async Task Clearing_keeps_the_lifetime_block_count()
    {
        // History, not state. How often a client has had to be stopped is the pattern that says it
        // needs a conversation rather than another reset, and clearing it hides that.
        await GivenClientAsync("ip:10.0.0.11", blocked: true, requestCount: 500, totalBlockedCount: 7);

        await ResetAsync("ip:10.0.0.11");

        Assert.Equal(7, (await ReadAsync("ip:10.0.0.11")).TotalBlockedCount);
    }

    [Fact]
    public async Task An_unknown_client_is_reported_rather_than_invented()
    {
        // False, not a created row: the endpoint answers 404, and a client nobody has counted has
        // nothing to clear.
        Assert.False(await ResetAsync("ip:never-seen"));

        using var context = NewContext();
        Assert.Empty(context.ApiRateLimits.Where(limit => limit.ClientId == "ip:never-seen"));
    }

    private Task<bool> ResetAsync(string clientId) => NewService().ResetClientAsync(clientId);

    private RateLimitService NewService()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimit:PermitLimit"] = "100",
            ["RateLimit:WindowSeconds"] = "60"
        }).Build();

        var store = new RateLimitConfigStore(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            NullLogger<RateLimitConfigStore>.Instance);

        return new RateLimitService(NewContext(), store, NullLogger<RateLimitService>.Instance);
    }

    private async Task GivenClientAsync(
        string clientId,
        bool blocked,
        int requestCount,
        int totalBlockedCount = 0,
        DateTime? windowStart = null)
    {
        using var context = NewContext();
        context.ApiRateLimits.Add(new ApiRateLimitEntity
        {
            ClientId = clientId,
            ClientType = "IP",
            RequestCount = requestCount,
            WindowStart = windowStart ?? DateTime.UtcNow,
            WindowDurationSeconds = 60,
            MaxRequests = 100,
            IsBlocked = blocked,
            BlockExpiresAt = blocked ? DateTime.UtcNow.AddMinutes(15) : null,
            TotalBlockedCount = totalBlockedCount,
            LastRequestAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private async Task<ApiRateLimitEntity> ReadAsync(string clientId)
    {
        using var context = NewContext();
        return await context.ApiRateLimits.AsNoTracking().SingleAsync(limit => limit.ClientId == clientId);
    }

    private ApplicationDbContext NewContext() =>
        new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

    /// <summary>
    /// <c>DailyStockSnapshotItem.Version</c> maps to Postgres' <c>xmin</c>, which SQLite has no
    /// equivalent for. Nothing here touches that entity.
    /// </summary>
    private sealed class SqliteApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<DailyStockSnapshotItemEntity>()
                .Property(item => item.Version)
                .ValueGeneratedNever()
                .IsConcurrencyToken(false);
        }
    }
}
