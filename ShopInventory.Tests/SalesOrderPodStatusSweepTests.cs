using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins how Mobile Orders walks its POD status batches.
/// </summary>
/// <remarks>
/// On 2026-09-16 one visit to the page sent 36 batches back to back while SAP's database was
/// unreachable. Every batch failed, and the loop went on to the next one regardless for six minutes.
/// </remarks>
public class SalesOrderPodStatusSweepTests
{
    private static readonly int[] ThreeBatches = Enumerable.Range(1, 250).ToArray();

    [Fact]
    public async Task Every_batch_is_asked_when_SAP_answers()
    {
        var calls = new List<IReadOnlyList<int>>();

        var completed = await SalesOrderPodStatusSweep.RunAsync(
            ThreeBatches,
            (batch, _) => Answer(calls, batch, lookupFailed: false),
            _ => Task.FromResult(true),
            CancellationToken.None);

        Assert.True(completed);
        Assert.Equal([100, 100, 50], calls.Select(batch => batch.Count));
    }

    [Fact]
    public async Task A_batch_SAP_could_not_look_up_ends_the_sweep()
    {
        var calls = new List<IReadOnlyList<int>>();
        var shown = new List<BulkPodValidationResult>();

        var completed = await SalesOrderPodStatusSweep.RunAsync(
            ThreeBatches,
            (batch, _) => Answer(calls, batch, lookupFailed: true),
            results =>
            {
                shown.AddRange(results);
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.False(completed);
        Assert.Single(calls);
        // The failed batch's results still reach the page before it stops.
        Assert.Equal(100, shown.Count);
    }

    [Fact]
    public async Task A_request_that_fails_outright_ends_the_sweep()
    {
        var calls = 0;

        var completed = await SalesOrderPodStatusSweep.RunAsync(
            ThreeBatches,
            (_, _) =>
            {
                calls++;
                return Task.FromResult<BulkPodValidationResponse?>(null);
            },
            _ => Task.FromResult(true),
            CancellationToken.None);

        Assert.False(completed);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task The_page_can_stop_the_sweep_between_batches()
    {
        var calls = new List<IReadOnlyList<int>>();

        var completed = await SalesOrderPodStatusSweep.RunAsync(
            ThreeBatches,
            (batch, _) => Answer(calls, batch, lookupFailed: false),
            _ => Task.FromResult(false),
            CancellationToken.None);

        Assert.False(completed);
        Assert.Single(calls);
    }

    [Fact]
    public async Task A_cancelled_sweep_asks_for_nothing_more()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = new List<IReadOnlyList<int>>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SalesOrderPodStatusSweep.RunAsync(
            ThreeBatches,
            (batch, _) => Answer(calls, batch, lookupFailed: false),
            _ =>
            {
                // Leaving the page after the first batch lands.
                cancellation.Cancel();
                return Task.FromResult(true);
            },
            cancellation.Token));

        Assert.Single(calls);
    }

    private static Task<BulkPodValidationResponse?> Answer(
        List<IReadOnlyList<int>> calls,
        IReadOnlyList<int> batch,
        bool lookupFailed)
    {
        calls.Add(batch);
        return Task.FromResult<BulkPodValidationResponse?>(new BulkPodValidationResponse
        {
            Results = batch.Select(docNum => new BulkPodValidationResult
            {
                SalesOrderDocNum = docNum,
                LookupFailed = lookupFailed
            }).ToList()
        });
    }
}
