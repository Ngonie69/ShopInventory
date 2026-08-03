using ErrorOr;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.Statements;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the property that makes a slow statement produceable at all: the build outlives the
/// request that started it.
/// </summary>
/// <remarks>
/// A controller's CancellationToken is HttpContext.RequestAborted, and the statement handler used to
/// pass it straight through to the SAP client. So when the portal's HttpClient gave up at 300
/// seconds the API discarded everything it had computed, and the customer's retry started from
/// nothing — which is why a statement that took longer than one attempt could never be produced by
/// any number of attempts. These tests pin the two halves of the fix: a caller walking away does not
/// cancel the build, and a build already running is joined rather than duplicated.
/// </remarks>
public class StatementBuildCacheTests
{
    [Fact]
    public async Task A_caller_giving_up_does_not_cancel_the_build_and_the_result_is_still_cached()
    {
        var cache = NewCache();
        var release = new TaskCompletionSource();
        var buildStarted = new TaskCompletionSource();
        var tokenTheBuildSaw = CancellationToken.None;

        Task<ErrorOr<CustomerStatementResponseDto>> Build(CancellationToken token)
        {
            tokenTheBuildSaw = token;
            buildStarted.TrySetResult();
            return release.Task.ContinueWith(_ => (ErrorOr<CustomerStatementResponseDto>)Statement("ABS006"));
        }

        using var callerGaveUp = new CancellationTokenSource();
        var waiting = cache.GetOrBuildAsync("key", Build, callerGaveUp.Token);

        await buildStarted.Task;
        callerGaveUp.Cancel();

        // The caller is released...
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        // ...but the build never saw the cancellation, and finishing still populates the cache.
        Assert.False(tokenTheBuildSaw.IsCancellationRequested);
        release.SetResult();

        // The customer's retry. It never rebuilds: it either joins the build still finishing or
        // reads what that build cached, and either way it is answered.
        var rebuilt = false;
        var afterTheRetry = await cache.GetOrBuildAsync(
            "key",
            _ =>
            {
                rebuilt = true;
                return Task.FromResult<ErrorOr<CustomerStatementResponseDto>>(Statement("REBUILT"));
            },
            CancellationToken.None);

        Assert.False(rebuilt, "the retry started a second build instead of using the abandoned one");
        Assert.Equal("ABS006", afterTheRetry.Value.Customer.CardCode);
    }

    [Fact]
    public async Task A_second_caller_joins_the_running_build_instead_of_starting_another()
    {
        var cache = NewCache();
        var release = new TaskCompletionSource();
        var builds = 0;

        Task<ErrorOr<CustomerStatementResponseDto>> Build(CancellationToken _)
        {
            Interlocked.Increment(ref builds);
            return release.Task.ContinueWith(_ => (ErrorOr<CustomerStatementResponseDto>)Statement("ABS006"));
        }

        var first = cache.GetOrBuildAsync("key", Build, CancellationToken.None);
        var second = cache.GetOrBuildAsync("key", Build, CancellationToken.None);

        release.SetResult();
        await Task.WhenAll(first, second);

        // An impatient customer clicking Generate twice must not put two identical SAP fan-outs
        // through the six shared concurrency slots.
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task A_completed_statement_is_served_from_the_cache_without_rebuilding()
    {
        var cache = NewCache();
        var builds = 0;

        Task<ErrorOr<CustomerStatementResponseDto>> Build(CancellationToken _)
        {
            Interlocked.Increment(ref builds);
            return Task.FromResult<ErrorOr<CustomerStatementResponseDto>>(Statement("ABS006"));
        }

        await cache.GetOrBuildAsync("key", Build, CancellationToken.None);
        await cache.GetOrBuildAsync("key", Build, CancellationToken.None);

        Assert.Equal(1, builds);
    }

    /// <summary>
    /// A customer who does not exist, or a momentarily unavailable SAP, must not become the answer
    /// for the next five minutes.
    /// </summary>
    [Fact]
    public async Task A_failed_build_is_not_cached()
    {
        var cache = NewCache();
        var builds = 0;

        Task<ErrorOr<CustomerStatementResponseDto>> Build(CancellationToken _)
        {
            Interlocked.Increment(ref builds);
            return Task.FromResult<ErrorOr<CustomerStatementResponseDto>>(Errors.Statement.CustomerNotFound("NOPE"));
        }

        var first = await cache.GetOrBuildAsync("key", Build, CancellationToken.None);
        var second = await cache.GetOrBuildAsync("key", Build, CancellationToken.None);

        Assert.True(first.IsError);
        Assert.True(second.IsError);
        Assert.Equal(2, builds);
    }

    /// <summary>
    /// A build that throws must reach whoever is waiting, and must not pin the key against a retry.
    /// </summary>
    [Fact]
    public async Task A_throwing_build_surfaces_to_the_caller_and_is_retried_next_time()
    {
        var cache = NewCache();
        var builds = 0;

        Task<ErrorOr<CustomerStatementResponseDto>> Build(CancellationToken _)
        {
            Interlocked.Increment(ref builds);
            throw new InvalidOperationException("SAP said no");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrBuildAsync("key", Build, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrBuildAsync("key", Build, CancellationToken.None));

        Assert.Equal(2, builds);
    }

    [Fact]
    public async Task Different_customers_and_date_ranges_do_not_share_a_result()
    {
        var cache = NewCache();

        var first = await cache.GetOrBuildAsync(
            "statement:ABS006:ABS006:2026-07-01:2026-07-31",
            _ => Task.FromResult<ErrorOr<CustomerStatementResponseDto>>(Statement("ABS006")),
            CancellationToken.None);
        var second = await cache.GetOrBuildAsync(
            "statement:OTHER:OTHER:2026-07-01:2026-07-31",
            _ => Task.FromResult<ErrorOr<CustomerStatementResponseDto>>(Statement("OTHER")),
            CancellationToken.None);

        Assert.Equal("ABS006", first.Value.Customer.CardCode);
        Assert.Equal("OTHER", second.Value.Customer.CardCode);
    }

    private static StatementBuildCache NewCache() =>
        new(new MemoryCache(new MemoryCacheOptions()), NullLogger<StatementBuildCache>.Instance);

    private static CustomerStatementResponseDto Statement(string cardCode) => new()
    {
        Customer = new StatementCustomerDto { CardCode = cardCode, CardName = cardCode }
    };
}
