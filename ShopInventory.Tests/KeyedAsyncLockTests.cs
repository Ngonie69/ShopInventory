using ShopInventory.Common.Caching;

namespace ShopInventory.Tests;

/// <summary>
/// The per-key lock behind ReportService's cache loads. It must still serialise one key, leave other
/// keys alone, and — the reason it exists — hold nothing once nobody is using a key.
/// </summary>
public class KeyedAsyncLockTests
{
    [Fact]
    public async Task Second_caller_for_a_key_waits_until_the_first_releases()
    {
        var locks = new KeyedAsyncLock();
        var first = await locks.AcquireAsync("report-data:stock|WH01");

        var second = locks.AcquireAsync("report-data:stock|WH01");
        await Task.Delay(100);
        Assert.False(second.IsCompleted);

        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Different_keys_do_not_block_each_other()
    {
        var locks = new KeyedAsyncLock();
        using var held = await locks.AcquireAsync("a");

        var other = locks.AcquireAsync("b");

        (await other.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Nothing_is_kept_once_every_key_is_released()
    {
        var locks = new KeyedAsyncLock();

        for (var day = 0; day < 500; day++)
        {
            using (await locks.AcquireAsync($"report-data:sales-summary|2026-01-01|{day}"))
            {
                Assert.Equal(1, locks.Count);
            }
        }

        Assert.Equal(0, locks.Count);
    }

    [Fact]
    public async Task Entry_survives_while_a_waiter_is_queued_and_goes_with_the_last()
    {
        var locks = new KeyedAsyncLock();
        var first = await locks.AcquireAsync("k");
        var second = locks.AcquireAsync("k");

        first.Dispose();
        Assert.Equal(1, locks.Count);

        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        Assert.Equal(0, locks.Count);
    }

    [Fact]
    public async Task Cancelled_waiter_leaves_no_entry_and_takes_no_turn()
    {
        var locks = new KeyedAsyncLock();
        var holder = await locks.AcquireAsync("k");

        using var cancel = new CancellationTokenSource();
        var waiter = locks.AcquireAsync("k", cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

        // Still held by the first caller: a third must wait.
        var third = locks.AcquireAsync("k");
        await Task.Delay(100);
        Assert.False(third.IsCompleted);

        holder.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        Assert.Equal(0, locks.Count);
    }

    [Fact]
    public async Task Disposing_twice_releases_once()
    {
        var locks = new KeyedAsyncLock();
        var first = await locks.AcquireAsync("k");
        var second = locks.AcquireAsync("k");
        var third = locks.AcquireAsync("k");

        first.Dispose();
        first.Dispose();

        var secondHandle = await second.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.False(third.IsCompleted);

        secondHandle.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        Assert.Equal(0, locks.Count);
    }

    [Fact]
    public async Task Many_callers_on_one_key_run_one_at_a_time()
    {
        var locks = new KeyedAsyncLock();
        var inside = 0;
        var maxInside = 0;

        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            using (await locks.AcquireAsync("k"))
            {
                var now = Interlocked.Increment(ref inside);
                InterlockedMax(ref maxInside, now);
                await Task.Delay(1);
                Interlocked.Decrement(ref inside);
            }
        })));

        Assert.Equal(1, maxInside);
        Assert.Equal(0, locks.Count);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target))
            && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
