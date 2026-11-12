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
                InnerHandler = new ReachSap(TimeProvider.System) { InnerHandler = seen }
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
    public async Task A_budgeted_report_read_SAP_never_answers_times_out_at_its_budget_and_records_nothing()
    {
        // The customer statement's reads. A stuck one used to hold its slot, and the customer, for the
        // whole five-minute client timeout. Unlike a stock read it is not counted: a few statements
        // must not be able to open the breaker and refuse every posting.
        var harness = new Harness(StatementLedgerHangs);
        var budget = TimeSpan.FromSeconds(30);

        var read = harness.Client.ExecuteParameterisedSqlQueryWithBudgetAsync(
            StatementLedgerCode,
            "Statement Ledger Rows",
            StatementLedgerSql,
            new Dictionary<string, string> { ["cardCode"] = "C1" },
            budget);
        Assert.True(await harness.Sap.WaitForHangAsync(read), "The ledger read was never sent.");
        harness.Clock.Advance(budget);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => read);
        Assert.Contains(StatementLedgerCode, ex.Message);
        Assert.Equal(1, harness.Sap.CancelledRequests);
        Assert.Equal(0, harness.Breaker.GetSnapshot().ConsecutiveFailures);
    }

    [Fact]
    public async Task A_budgeted_report_read_is_not_cut_short_before_its_budget()
    {
        var harness = new Harness(StatementLedgerHangs);
        var budget = TimeSpan.FromSeconds(30);

        var read = harness.Client.ExecuteParameterisedSqlQueryWithBudgetAsync(
            StatementLedgerCode,
            "Statement Ledger Rows",
            StatementLedgerSql,
            new Dictionary<string, string> { ["cardCode"] = "C1" },
            budget);
        await harness.Sap.WaitForHangAsync(read);
        harness.Clock.Advance(budget - TimeSpan.FromSeconds(1));

        // A cancellation, had one fired, unwinds through HttpClient asynchronously; give it a moment
        // so "still running" is an observation rather than a race.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.False(read.IsCompleted);
        Assert.Equal(0, harness.Sap.CancelledRequests);

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => read);
    }

    [Fact]
    public async Task A_caller_giving_up_on_a_budgeted_report_read_is_a_cancellation_not_a_timeout()
    {
        var harness = new Harness(StatementLedgerHangs);
        using var caller = new CancellationTokenSource();

        var read = harness.Client.ExecuteParameterisedSqlQueryWithBudgetAsync(
            StatementLedgerCode,
            "Statement Ledger Rows",
            StatementLedgerSql,
            new Dictionary<string, string> { ["cardCode"] = "C1" },
            TimeSpan.FromSeconds(30),
            caller.Token);
        await harness.Sap.WaitForHangAsync(read);
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(1, harness.Sap.CancelledRequests);
        Assert.Equal(0, harness.Breaker.GetSnapshot().ConsecutiveFailures);
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

    [Fact]
    public async Task A_background_stock_read_SAP_is_still_working_on_is_refused_at_once_and_not_counted()
    {
        // SAP keeps working on a read this client gave up on. Background work asking for the same read
        // again would stack a second stuck Service Layer process beside the first, so the guard refuses
        // it before it takes a slot: no request reaches SAP, no retry sleeps on it, and the breaker,
        // which sits inside the guard, sees only the one timeout that really happened.
        var harness = new Harness(stockBudget: TimeSpan.FromSeconds(60));

        var first = harness.Client.GetStockQuantitiesInWarehouseAsync("CORMACH");
        await harness.Sap.WaitForHangAsync(first);
        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        await Assert.ThrowsAsync<TimeoutException>(() => first);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var ex = await Assert.ThrowsAsync<SapReadStillRunningException>(
            () => harness.Client.GetStockQuantitiesInWarehouseAsync("CORMACH"));

        // A retried attempt would have slept 2 s and then 4 s first.
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1.5),
            "The held read was retried rather than refused.");
        Assert.Equal(harness.Clock.GetUtcNow() + TimeSpan.FromMinutes(10), ex.HeldUntil);
        Assert.Equal(1, harness.Sap.HungRequests);
        Assert.Equal(1, harness.Breaker.GetSnapshot().ConsecutiveFailures);
    }

    [Fact]
    public async Task A_background_stock_read_whose_family_SAP_is_still_busy_with_is_refused_at_once_and_not_counted()
    {
        // Another warehouse, so the stuck-read hold does not apply: the family limit does. SAP is presumed
        // to be still working on the first read, so its slot is kept, and the second read is refused
        // before it reaches SAP or the breaker, and without the transient retry's sleeps.
        var harness = new Harness(stockBudget: TimeSpan.FromSeconds(60), stockQtyLimit: 1);

        var first = harness.Client.GetStockQuantitiesInWarehouseAsync("CORMACH");
        await harness.Sap.WaitForHangAsync(first);
        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        await Assert.ThrowsAsync<TimeoutException>(() => first);

        // Bounded: sent instead of refused, the read would hang at SAP on a clock nothing moves.
        var ex = await Assert.ThrowsAsync<SapReadFamilyFullException>(
            () => harness.Client.GetStockQuantitiesInWarehouseAsync("KEFSHOP").WaitAsync(WaitLimit));

        // Counted where the read enters the pipeline rather than timed: a retried read would enter three
        // times, and a wall-clock bound on "no retry sleeps" fails on a busy machine.
        Assert.Equal(1, harness.Entered.Count(path => path.Contains("STOCK_QTY_KEFSHOP')/List", StringComparison.Ordinal)));
        Assert.Equal("STOCK_QTY", ex.Family);
        Assert.Equal(harness.Clock.GetUtcNow() + TimeSpan.FromMinutes(10), ex.FirstSlotFreesAt);
        Assert.Equal(1, harness.Sap.HungRequests);
        // KEFSHOP's query was looked up and created first, and those answers reset the count the first
        // read's timeout left; the refusal, had the breaker counted it, would show here as one.
        Assert.Equal(0, harness.Breaker.GetSnapshot().ConsecutiveFailures);
    }

    [Fact]
    public async Task A_stock_read_a_person_is_waiting_on_is_sent_even_while_held()
    {
        var harness = new Harness(stockBudget: TimeSpan.FromSeconds(60));

        var first = harness.Client.GetStockQuantitiesInWarehouseAsync("CORMACH");
        await harness.Sap.WaitForHangAsync(first);
        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        await Assert.ThrowsAsync<TimeoutException>(() => first);

        using (SapRequestPriority.BeginInteractive())
        {
            var second = harness.Client.GetStockQuantitiesInWarehouseAsync("CORMACH");
            Assert.True(await harness.Sap.WaitForHangAsync(second), "The person's read was not sent to SAP.");
            harness.Clock.Advance(TimeSpan.FromSeconds(60));
            await Assert.ThrowsAsync<TimeoutException>(() => second);
        }

        Assert.Equal(2, harness.Sap.HungRequests);
    }

    [Fact]
    public async Task A_read_that_outlives_the_client_timeout_is_not_sent_again()
    {
        // HttpClient's own timeout means SAP held the request for the whole client deadline. Sending it
        // again held the slot for a second and a third deadline: fifteen minutes in one of the six slots
        // at the production five-minute timeout, for callers such as jobs that never cancel.
        var harness = new Harness(
            path => path.Contains("/Invoices(", StringComparison.Ordinal),
            clientTimeout: TimeSpan.FromMilliseconds(300));

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(
            () => harness.Client.GetInvoiceByDocEntryAsync(1));

        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Equal(1, harness.Sap.HungRequests);
    }

    private const string StatementLedgerCode = "STMT_LEDGER_ROWS";

    private const string StatementLedgerSql =
        "SELECT T1.\"Debit\" FROM \"JDT1\" T1 WHERE T1.\"ShortName\" = :cardCode";

    private static bool StatementLedgerHangs(string path) =>
        path.Contains(StatementLedgerCode, StringComparison.Ordinal)
        && path.EndsWith("/List", StringComparison.Ordinal);

    private static bool BatchListHangs(string path) =>
        path.Contains(SAPServiceLayerClient.ItemBatchesQueryCode, StringComparison.Ordinal)
        && path.EndsWith("/List", StringComparison.Ordinal);

    private static SapCircuitBreakerState NewBreaker() =>
        new(Options.Create(new SAPSettings { CircuitFailureThreshold = 5, CircuitBreakDurationSeconds = 30 }));

    /// <summary>
    /// The SAP client wired the way Program.cs wires it — the stuck-read guard outermost, then the
    /// breaker, then a stand-in for the concurrency handler — over a Service Layer that leaves chosen
    /// reads unanswered.
    /// </summary>
    /// <remarks>
    /// The default stock budget is shorter than <see cref="SapStuckReadState.MinimumTimeAtSap"/>, so the
    /// guard stays out of the way of the tests that repeat a read on purpose.
    /// </remarks>
    private sealed class Harness
    {
        public static readonly TimeSpan StockBudget = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan PriceListBudget = TimeSpan.FromSeconds(10);
        /// <param name="stockQtyLimit">
        /// When above zero, <see cref="SAPReadFamilyHandler"/> sits between the stuck-read handler and the
        /// breaker, as in production, limiting <c>STOCK_QTY</c> reads to this many; otherwise it is left out.
        /// </param>
        public Harness(
            Func<string, bool>? hangs = null,
            TimeSpan? clientTimeout = null,
            TimeSpan? stockBudget = null,
            int stockQtyLimit = 0)
        {
            var timeout = clientTimeout ?? TimeSpan.FromMinutes(5);
            Breaker = NewBreaker();
            var pipelineSettings = Options.Create(new SAPSettings
            {
                StuckReadHoldMinutes = 10,
                ReadFamilies = new() { ["STOCK_QTY"] = new() { MaxConcurrentReads = stockQtyLimit } },
                ReadFamilyBackgroundMaxWaitSeconds = 5,
            });
            StuckReads = new SapStuckReadState(pipelineSettings, Clock);
            Sap = new HangingSap(hangs ?? (path => path.Contains("STOCK_QTY_", StringComparison.Ordinal)
                                                   && path.EndsWith("/List", StringComparison.Ordinal)));

            HttpMessageHandler belowStuckReads = new SAPCircuitBreakerHandler(Breaker, NullLogger<SAPCircuitBreakerHandler>.Instance, timeout)
            {
                InnerHandler = new ReachSap(Clock) { InnerHandler = Sap }
            };
            if (stockQtyLimit > 0)
            {
                belowStuckReads = new SAPReadFamilyHandler(
                    new SapReadFamilyGate(pipelineSettings, Clock),
                    StuckReads,
                    Clock,
                    NullLogger<SAPReadFamilyHandler>.Instance)
                {
                    InnerHandler = belowStuckReads
                };
            }

            var httpClient = new HttpClient(
                new EntryRecorder(Entered)
                {
                    InnerHandler = new SAPStuckReadHandler(StuckReads, NullLogger<SAPStuckReadHandler>.Instance)
                    {
                        InnerHandler = belowStuckReads
                    }
                })
            {
                BaseAddress = new Uri("https://sap.invalid/b1s/v1/"),
                Timeout = timeout
            };

            var services = new ServiceCollection().BuildServiceProvider();

            Client = new SAPServiceLayerClient(
                httpClient,
                new SingleClientFactory(httpClient),
                Options.Create(new SAPSettings
                {
                    ServiceLayerUrl = "https://sap.invalid/b1s/v1/",
                    StockSqlRequestTimeoutSeconds = (int)(stockBudget ?? StockBudget).TotalSeconds,
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
        public SapStuckReadState StuckReads { get; }

        /// <summary>The path of every request that entered the SAP pipeline, retries included.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<string> Entered { get; } = new();
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
    private sealed class ReachSap(TimeProvider clock) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SapRequestMarks.MarkReachedSap(request, clock.GetUtcNow());
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Outermost: records each request as it enters the SAP pipeline, so a retry is counted.</summary>
    private sealed class EntryRecorder(System.Collections.Concurrent.ConcurrentQueue<string> entered) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            entered.Enqueue(request.RequestUri!.AbsolutePath);
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
