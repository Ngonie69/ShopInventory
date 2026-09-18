using System.Net;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.Middleware;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins which timeouts the SAP circuit breaker counts as failures.
/// </summary>
/// <remarks>
/// Every timeout reaches the breaker handler as a cancelled token, exactly like a caller that gave
/// up, and the breaker used to read both as the caller. So it opened for 5xx and dropped
/// connections but never for SAP accepting a request and not answering — stock reads hanging for
/// 60 to 265 seconds, each holding one of the six concurrency slots for its whole budget.
/// </remarks>
[Collection("SapServiceLayerClient")]
public class SapCircuitBreakerTimeoutTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task HttpClients_own_timeout_counts_and_the_handler_sees_only_a_cancelled_token()
    {
        var state = NewBreaker();
        var sap = new HangingSap(_ => true);
        var seen = new ExceptionRecorder { InnerHandler = sap };
        var clientTimeout = TimeSpan.FromMilliseconds(300);
        using var client = new HttpClient(
            new SAPCircuitBreakerHandler(state, NullLogger<SAPCircuitBreakerHandler>.Instance, clientTimeout)
            {
                InnerHandler = new ReachSap { InnerHandler = seen }
            })
        {
            Timeout = clientTimeout
        };

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(
            () => client.GetAsync("https://sap.invalid/b1s/v1/Items('A')"));

        // What the caller gets is HttpClient's timeout form...
        Assert.IsType<TimeoutException>(ex.InnerException);
        // ...but inside the handler chain it was a bare cancellation, which is why the handler has
        // to be told the client's deadline rather than read the exception.
        Assert.NotNull(seen.Exception);
        Assert.IsNotType<TimeoutException>(seen.Exception!.InnerException);
        Assert.Equal(1, state.GetSnapshot().ConsecutiveFailures);
    }

    [Fact]
    public async Task A_timeout_while_still_queued_for_a_slot_is_not_counted()
    {
        // No ReachSap: the request never got past the concurrency handler, so SAP was never asked.
        var state = NewBreaker();
        var clientTimeout = TimeSpan.FromMilliseconds(300);
        using var client = new HttpClient(
            new SAPCircuitBreakerHandler(state, NullLogger<SAPCircuitBreakerHandler>.Instance, clientTimeout)
            {
                InnerHandler = new HangingSap(_ => true)
            })
        {
            Timeout = clientTimeout
        };

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => client.GetAsync("https://sap.invalid/b1s/v1/Items('A')"));

        Assert.Equal(0, state.GetSnapshot().ConsecutiveFailures);
    }

    [Fact]
    public async Task A_stock_read_SAP_never_answers_records_exactly_one_failure()
    {
        var harness = new Harness();

        var read = harness.Client.GetStockQuantitiesInWarehouseAsync("CORMACH");
        await harness.Sap.WaitForHangAsync(read);
        harness.Clock.Advance(Harness.StockBudget);

        await Assert.ThrowsAsync<TimeoutException>(() => read);
        var snapshot = harness.Breaker.GetSnapshot();
        Assert.Equal(1, snapshot.ConsecutiveFailures);
        Assert.Contains("deadline", snapshot.LastFailure);
        Assert.Equal(1, harness.Sap.HungRequests);
    }

    [Fact]
    public async Task A_caller_giving_up_on_a_stock_read_records_nothing()
    {
        var harness = new Harness();
        using var caller = new CancellationTokenSource();

        var read = harness.Client.GetStockQuantitiesInWarehouseAsync("CORMACH", caller.Token);
        await harness.Sap.WaitForHangAsync(read);
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(0, harness.Breaker.GetSnapshot().ConsecutiveFailures);
    }

    [Fact]
    public async Task A_batch_read_SAP_never_answers_records_exactly_one_failure()
    {
        // The per-item batch read runs its SQL under the stock budget too, through the raw SQL path
        // rather than SendStockRequestWithBudgetAsync, so it has to mark its requests itself.
        var harness = new Harness(BatchListHangs);

        var read = harness.Client.GetBatchNumbersForItemInWarehouseAsync("CHE011", "KEFBYC");
        await harness.Sap.WaitForHangAsync(read);
        harness.Clock.Advance(Harness.StockBudget);

        await Assert.ThrowsAsync<TimeoutException>(() => read);
        var snapshot = harness.Breaker.GetSnapshot();
        Assert.Equal(1, snapshot.ConsecutiveFailures);
        Assert.Contains("deadline", snapshot.LastFailure);
        Assert.Equal(1, harness.Sap.HungRequests);
    }

    [Fact]
    public async Task A_caller_giving_up_on_a_batch_read_records_nothing()
    {
        var harness = new Harness(BatchListHangs);
        using var caller = new CancellationTokenSource();

        var read = harness.Client.GetBatchNumbersForItemInWarehouseAsync("CHE011", "KEFBYC", caller.Token);
        await harness.Sap.WaitForHangAsync(read);
        caller.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.IsNotType<TimeoutException>(ex);
        Assert.Equal(1, harness.Sap.CancelledRequests);
        Assert.Equal(0, harness.Breaker.GetSnapshot().ConsecutiveFailures);
    }

    [Fact]
    public async Task The_price_list_budget_running_out_records_nothing()
    {
        // Both price-list reads — the PriceLists entity set and the SQL fallback over OPLN — run
        // under the price-list budget. Neither expiry may count: the budget is tight on purpose and
        // the sync has fallbacks of its own.
        var harness = new Harness(path =>
            path.EndsWith("/PriceLists", StringComparison.Ordinal)
            || path.Contains("SHOP_PRICE_LISTS", StringComparison.Ordinal) && path.EndsWith("/List", StringComparison.Ordinal));

        var load = harness.Client.GetPriceListsAsync();
        while (await harness.Sap.WaitForHangAsync(load))
        {
            harness.Clock.Advance(Harness.PriceListBudget);
        }

        try
        {
            await load;
        }
        catch (Exception)
        {
            // Whatever the sync falls back to is not this test's concern.
        }

        Assert.True(harness.Sap.HungRequests >= 1, "No price-list read was left hanging, so no budget ran out.");
        Assert.True(harness.Sap.CancelledRequests >= 1, "No price-list budget was spent.");
        var snapshot = harness.Breaker.GetSnapshot();
        Assert.True(snapshot.ConsecutiveFailures == 0, $"Recorded {snapshot.ConsecutiveFailures} failure(s), last: {snapshot.LastFailure}");
        Assert.False(harness.Breaker.IsOpen);
    }

    [Fact]
    public async Task Five_stock_budgets_running_out_in_a_row_open_the_breaker()
    {
        var harness = new Harness();

        for (var i = 0; i < 5; i++)
        {
            var read = harness.Client.GetStockQuantitiesInWarehouseAsync("CORMACH");
            await harness.Sap.WaitForHangAsync(read);
            harness.Clock.Advance(Harness.StockBudget);
            await Assert.ThrowsAsync<TimeoutException>(() => read);
        }

        Assert.True(harness.Breaker.IsOpen);

        await Assert.ThrowsAsync<SapCircuitOpenException>(
            () => harness.Client.GetStockQuantitiesInWarehouseAsync("CORMACH"));
        Assert.Equal(5, harness.Sap.HungRequests);
    }

    private static bool BatchListHangs(string path) =>
        path.Contains(SAPServiceLayerClient.ItemBatchesQueryCode, StringComparison.Ordinal)
        && path.EndsWith("/List", StringComparison.Ordinal);

    private static SapCircuitBreakerState NewBreaker() =>
        new(Options.Create(new SAPSettings { CircuitFailureThreshold = 5, CircuitBreakDurationSeconds = 30 }));

    /// <summary>
    /// The SAP client wired the way Program.cs wires it — breaker outermost, then a stand-in for the
    /// concurrency handler — over a Service Layer that leaves chosen reads unanswered.
    /// </summary>
    private sealed class Harness
    {
        public static readonly TimeSpan StockBudget = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan PriceListBudget = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ClientTimeout = TimeSpan.FromMinutes(5);

        public Harness(Func<string, bool>? hangs = null)
        {
            Breaker = NewBreaker();
            Sap = new HangingSap(hangs ?? (path => path.Contains("STOCK_QTY_", StringComparison.Ordinal)
                                                   && path.EndsWith("/List", StringComparison.Ordinal)));

            var httpClient = new HttpClient(
                new SAPCircuitBreakerHandler(Breaker, NullLogger<SAPCircuitBreakerHandler>.Instance, ClientTimeout)
                {
                    InnerHandler = new ReachSap { InnerHandler = Sap }
                })
            {
                BaseAddress = new Uri("https://sap.invalid/b1s/v1/"),
                Timeout = ClientTimeout
            };

            var services = new ServiceCollection().BuildServiceProvider();

            Client = new SAPServiceLayerClient(
                httpClient,
                new SingleClientFactory(httpClient),
                Options.Create(new SAPSettings
                {
                    ServiceLayerUrl = "https://sap.invalid/b1s/v1/",
                    StockSqlRequestTimeoutSeconds = (int)StockBudget.TotalSeconds,
                    PriceListSqlRequestTimeoutSeconds = (int)PriceListBudget.TotalSeconds
                }),
                new StubHostEnvironment(),
                NullLogger<SAPServiceLayerClient>.Instance,
                new MemoryCache(new MemoryCacheOptions()),
                new CacheSyncStateRecorder(
                    services.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<CacheSyncStateRecorder>.Instance),
                StubProxy.Unused<ISapItemUomMappingStore>(),
                timeProvider: Clock);
        }

        public ManualClock Clock { get; } = new();
        public SapCircuitBreakerState Breaker { get; }
        public HangingSap Sap { get; }
        public SAPServiceLayerClient Client { get; }
    }

    /// <summary>
    /// Answers the login and SQL query provisioning, and leaves any request matching
    /// <paramref name="hangs"/> unanswered until it is cancelled.
    /// </summary>
    private sealed class HangingSap(Func<string, bool> hangs) : HttpMessageHandler
    {
        private readonly Channel<bool> _hangs = Channel.CreateUnbounded<bool>();
        private int _hungRequests;
        private int _cancelledRequests;

        public int HungRequests => Volatile.Read(ref _hungRequests);
        public int CancelledRequests => Volatile.Read(ref _cancelledRequests);

        /// <summary>
        /// Waits until a request is hanging, or <paramref name="call"/> finishes without one;
        /// returns whether a request is hanging.
        /// </summary>
        public async Task<bool> WaitForHangAsync(Task call)
        {
            var hang = _hangs.Reader.ReadAsync().AsTask();
            var finished = await Task.WhenAny(hang, call).WaitAsync(WaitLimit);
            return finished == hang;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/Login", StringComparison.Ordinal))
            {
                return Json("{\"SessionId\":\"test-session\"}");
            }

            if (hangs(path))
            {
                Interlocked.Increment(ref _hungRequests);
                _hangs.Writer.TryWrite(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref _cancelledRequests);
                    throw;
                }
            }

            if (path.EndsWith("/SQLQueries", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                return Json("{}", HttpStatusCode.Created);
            }

            if (path.EndsWith("/List", StringComparison.Ordinal))
            {
                return Json("{\"value\":[]}");
            }

            return Json("{}", HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }

    /// <summary>
    /// Stands in for SAPConcurrencyHandler, which marks a request once it holds a slot. The real
    /// one keeps its semaphores in statics shared with the SapConcurrency tests.
    /// </summary>
    private sealed class ReachSap : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SapRequestMarks.MarkReachedSap(request);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Records the exception that passes back up through this point of the chain.</summary>
    private sealed class ExceptionRecorder : DelegatingHandler
    {
        public Exception? Exception { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                Exception = ex;
                throw;
            }
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ShopInventory.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
