using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ShopInventory.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// Runs against a real PostgreSQL server, because advisory locks and the connection
/// pool are the behaviour under test. Set SHOPINVENTORY_TEST_POSTGRES to a connection
/// string for a database you can connect to (no tables are needed); without it these
/// tests are reported as skipped.
///
/// The bug these guard: a 27-line FEFO invoice asked for 54+ locks, each lock kept its
/// own pooled connection, and the API pool holds 40. The invoice drained the pool and
/// every other request failed until the locks expired 30 seconds later.
/// </summary>
public sealed class PostgresInventoryLockServiceTests
{
    private const string ConnectionVariable = "SHOPINVENTORY_TEST_POSTGRES";

    [PostgresFact]
    public async Task Many_locks_for_one_invoice_fit_in_a_small_pool()
    {
        var applicationName = NewApplicationName();
        var service = CreateService(applicationName, maximumPoolSize: 5);
        var requests = InvoiceLockRequests("KEFBYC", lines: 30);

        var result = await service.TryAcquireMultipleLocksAsync(requests, TimeSpan.FromSeconds(30));

        try
        {
            Assert.True(result.AllAcquired);
            Assert.Equal(60, result.LockTokens.Count);

            var held = await CountAdvisoryLocksAsync(applicationName);
            Assert.Equal(60, held.Locks);
            Assert.Equal(1, held.Sessions);
        }
        finally
        {
            await service.ReleaseMultipleLocksAsync(result.LockTokens);
        }

        Assert.Equal(0, (await CountAdvisoryLocksAsync(applicationName)).Locks);
    }

    [PostgresFact]
    public async Task A_refused_lock_releases_the_ones_already_taken()
    {
        var holderName = NewApplicationName();
        var holder = CreateService(holderName, maximumPoolSize: 5);
        var contenderName = NewApplicationName();
        var contender = CreateService(contenderName, maximumPoolSize: 5);
        var warehouse = UniqueWarehouse();

        // Lock keys are sorted before they are taken, so "M" is reached after A..L.
        var held = await holder.TryAcquireMultipleLocksAsync(
            [new InventoryLockRequest { ItemCode = "M", WarehouseCode = warehouse }]);

        try
        {
            Assert.True(held.AllAcquired);

            var requests = "ABCDEFGHIJKLMNOPQRSTUVWXYZ"
                .Select(item => new InventoryLockRequest { ItemCode = item.ToString(), WarehouseCode = warehouse })
                .ToList();

            var result = await contender.TryAcquireMultipleLocksAsync(requests);

            Assert.False(result.AllAcquired);
            Assert.Empty(result.LockTokens);
            var failure = Assert.Single(result.FailedLocks);
            Assert.Equal("M", failure.ItemCode);
            Assert.Equal(0, (await CountAdvisoryLocksAsync(contenderName)).Locks);
        }
        finally
        {
            await holder.ReleaseMultipleLocksAsync(held.LockTokens);
        }
    }

    [PostgresFact]
    public async Task A_lock_shuts_out_other_callers_until_it_is_released()
    {
        var first = CreateService(NewApplicationName(), maximumPoolSize: 5);
        var second = CreateService(NewApplicationName(), maximumPoolSize: 5);
        var warehouse = UniqueWarehouse();

        var held = await first.TryAcquireLockAsync("ITEM1", warehouse, "BATCH1");
        Assert.True(held.Acquired);

        var refused = await second.TryAcquireLockAsync("ITEM1", warehouse, "BATCH1");
        Assert.False(refused.Acquired);

        Assert.True(await first.ReleaseLockAsync(held.LockToken!));

        var retried = await second.TryAcquireLockAsync("ITEM1", warehouse, "BATCH1");
        Assert.True(retried.Acquired);
        await second.ReleaseLockAsync(retried.LockToken!);
    }

    [PostgresFact]
    public async Task Locks_that_expire_together_all_release()
    {
        var applicationName = NewApplicationName();
        var service = CreateService(applicationName, maximumPoolSize: 5);
        var requests = InvoiceLockRequests("VAN005", lines: 20);

        var result = await service.TryAcquireMultipleLocksAsync(requests, TimeSpan.FromSeconds(1));
        Assert.True(result.AllAcquired);
        Assert.Equal(40, (await CountAdvisoryLocksAsync(applicationName)).Locks);

        // All forty expiry timers fire at once and share one connection.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var remaining = int.MaxValue;
        while (DateTime.UtcNow < deadline)
        {
            remaining = (await CountAdvisoryLocksAsync(applicationName)).Locks;
            if (remaining == 0)
            {
                break;
            }

            await Task.Delay(200);
        }

        Assert.Equal(0, remaining);
        Assert.All(result.LockTokens, token => Assert.False(service.IsLockValidAsync(token).GetAwaiter().GetResult()));
    }

    [PostgresFact]
    public async Task Releasing_part_of_a_set_keeps_the_rest_held()
    {
        var applicationName = NewApplicationName();
        var service = CreateService(applicationName, maximumPoolSize: 5);
        var requests = InvoiceLockRequests("KEFGRS", lines: 3);

        var result = await service.TryAcquireMultipleLocksAsync(requests);
        Assert.True(result.AllAcquired);

        Assert.True(await service.ReleaseLockAsync(result.LockTokens[0]));
        Assert.Equal(5, (await CountAdvisoryLocksAsync(applicationName)).Locks);
        Assert.True(await service.IsLockValidAsync(result.LockTokens[1]));

        await service.ReleaseMultipleLocksAsync(result.LockTokens.Skip(1));
        Assert.Equal(0, (await CountAdvisoryLocksAsync(applicationName)).Locks);
    }

    /// <summary>One item+warehouse lock and one batch lock per line, as invoice validation asks.</summary>
    private static List<InventoryLockRequest> InvoiceLockRequests(string warehousePrefix, int lines)
    {
        var warehouse = $"{warehousePrefix}-{UniqueWarehouse()}";
        var requests = new List<InventoryLockRequest>();
        for (var line = 1; line <= lines; line++)
        {
            var item = $"ITEM{line:D3}";
            requests.Add(new InventoryLockRequest { ItemCode = item, WarehouseCode = warehouse });
            requests.Add(new InventoryLockRequest { ItemCode = item, WarehouseCode = warehouse, BatchNumber = $"B{line:D3}" });
        }

        return requests;
    }

    private static string UniqueWarehouse() => Guid.NewGuid().ToString("N")[..12];

    private static string NewApplicationName() => $"locktest-{Guid.NewGuid():N}"[..40];

    private static PostgresInventoryLockService CreateService(string applicationName, int maximumPoolSize)
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(ConnectionVariable))
        {
            ApplicationName = applicationName,
            MaxPoolSize = maximumPoolSize,
            MinPoolSize = 0,
            Timeout = 3
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = builder.ConnectionString
            })
            .Build();

        return new PostgresInventoryLockService(configuration, NullLogger<PostgresInventoryLockService>.Instance);
    }

    private static async Task<(int Locks, int Sessions)> CountAdvisoryLocksAsync(string applicationName)
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(ConnectionVariable))
        {
            Pooling = false
        };

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)::int, count(DISTINCT l.pid)::int
            FROM pg_locks l
            JOIN pg_stat_activity a ON a.pid = l.pid
            WHERE l.locktype = 'advisory' AND l.granted AND a.application_name = @app
            """,
            connection);
        command.Parameters.AddWithValue("app", applicationName);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
            {
                Skip = $"Set {ConnectionVariable} to a PostgreSQL connection string to run this test.";
            }
        }
    }
}
