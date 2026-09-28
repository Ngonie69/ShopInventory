using ShopInventory.Common;

namespace ShopInventory.Tests;

/// <summary>
/// One load per key at a time: callers that arrive while it runs share it, a failure is not kept,
/// and a caller that gives up leaves the load running for the rest.
/// </summary>
public sealed class SingleFlightTests
{
    [Fact]
    public async Task Callers_that_arrive_while_a_load_runs_share_it()
    {
        var flights = new SingleFlight();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;

        async Task<int> Load()
        {
            Interlocked.Increment(ref loads);
            return await release.Task;
        }

        var callers = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => flights.JoinAsync("whs:VAN001", Load, CancellationToken.None)))
            .ToList();
        await WaitUntilAsync(() => Volatile.Read(ref loads) > 0);
        await Task.Delay(100);

        release.SetResult(42);
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, loads);
        Assert.All(results, result => Assert.Equal(42, result));
        Assert.Equal(0, flights.Count);
    }

    [Fact]
    public async Task Different_keys_load_separately()
    {
        var flights = new SingleFlight();
        var loads = 0;

        async Task<string> Load(string value)
        {
            Interlocked.Increment(ref loads);
            await Task.Delay(50);
            return value;
        }

        var results = await Task.WhenAll(
            flights.JoinAsync("whs:VAN001", () => Load("one"), CancellationToken.None),
            flights.JoinAsync("whs:VAN002", () => Load("two"), CancellationToken.None));

        Assert.Equal(["one", "two"], results);
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task A_failure_reaches_every_waiter_and_the_next_caller_loads_again()
    {
        var flights = new SingleFlight();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = flights.JoinAsync("whs:VAN001", () => release.Task, CancellationToken.None);
        var second = flights.JoinAsync("whs:VAN001", () => Task.FromResult(-1), CancellationToken.None);
        release.SetException(new TimeoutException("SAP stock read exceeded its budget"));

        await Assert.ThrowsAsync<TimeoutException>(() => first);
        await Assert.ThrowsAsync<TimeoutException>(() => second);

        Assert.Equal(7, await flights.JoinAsync("whs:VAN001", () => Task.FromResult(7), CancellationToken.None));
    }

    [Fact]
    public async Task A_caller_that_gives_up_leaves_the_load_running_for_the_others()
    {
        var flights = new SingleFlight();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var impatient = new CancellationTokenSource();

        var leaving = flights.JoinAsync("whs:VAN001", () => release.Task, impatient.Token);
        var staying = flights.JoinAsync("whs:VAN001", () => Task.FromResult(-1), CancellationToken.None);

        impatient.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);

        release.SetResult(5);
        Assert.Equal(5, await staying);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
