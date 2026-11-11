using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Middleware;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins how many <c>STK_ITEMS</c> reads <see cref="SAPStockItemsReadHandler"/> lets reach SAP at once,
/// and that a read a person is waiting on is never kept queued for long.
/// </summary>
/// <remarks>
/// On KFLDB on 2026-11-11, 61 of the 101 <c>STK_ITEMS</c> reads that took over a minute began while three
/// or more others were already running on the Service Layer. Each gate here is its own instance, so
/// these tests can run alongside each other.
/// </remarks>
public class SapStockItemsReadGateTests
{
    private const string StockRead = "https://sap.invalid/b1s/v1/SQLQueries('STK_ITEMS_ABC123DEF456')/List?$skip=0";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task No_more_than_the_limit_reach_SAP_at_once()
    {
        var sap = new Sap(limit: 3);
        var reads = Enumerable.Range(0, 5).Select(_ => sap.SendAsync(StockRead)).ToList();

        await sap.WaitForArrivalsAsync(3);
        Assert.False(await sap.AnotherArrivesAsync(), "A fourth read reached SAP while three were running.");

        sap.AnswerOldest();
        await sap.WaitForArrivalsAsync(1);
        sap.AnswerOldest();
        await sap.WaitForArrivalsAsync(1);
        sap.AnswerAll();

        foreach (var read in reads)
        {
            using var response = await read;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(5, sap.Sent);
        Assert.Equal(3, sap.MostAtOnce);
        Assert.Equal(3, sap.Gate.Available);
    }

    [Fact]
    public async Task A_background_read_waits_until_its_budget_runs_out_without_reaching_SAP()
    {
        var sap = new Sap(limit: 1);
        var first = sap.SendAsync(StockRead);
        await sap.WaitForArrivalsAsync(1);

        using var budget = new CancellationTokenSource();
        var queued = sap.SendAsync(StockRead, cancellationToken: budget.Token);
        Assert.False(await sap.AnotherArrivesAsync());

        budget.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        sap.AnswerAll();
        using (await first) { }

        // The cancelled wait took no slot, so the next read goes straight through.
        var next = sap.SendAsync(StockRead);
        await sap.WaitForArrivalsAsync(1);
        sap.AnswerAll();
        using var answered = await next;
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.Equal(2, sap.Sent);
        Assert.Equal(1, sap.Gate.Available);
    }

    [Fact]
    public async Task A_read_a_person_is_waiting_on_is_sent_anyway_once_it_has_waited_the_maximum()
    {
        // The invoice stock guard fails closed: queued until its budget ran out, the invoice is refused.
        var sap = new Sap(limit: 1, interactiveMaxWaitSeconds: 10);
        var stuck = sap.SendAsync(StockRead);
        await sap.WaitForArrivalsAsync(1);

        Task<HttpResponseMessage> person;
        using (SapRequestPriority.BeginInteractive())
        {
            person = sap.SendAsync(StockRead);
        }

        sap.Clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(await sap.AnotherArrivesAsync(), "The person's read went before its maximum wait.");

        sap.Clock.Advance(TimeSpan.FromSeconds(1));
        await sap.WaitForArrivalsAsync(1);
        Assert.Equal(2, sap.MostAtOnce);

        sap.AnswerAll();
        using (var answered = await person)
        {
            Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        }

        using (await stuck) { }

        // Sent without a slot, it gives none back: the limit is still exactly one.
        Assert.Equal(1, sap.Gate.Available);
    }

    [Fact]
    public async Task A_read_a_person_is_waiting_on_takes_a_slot_that_frees_up_in_time()
    {
        var sap = new Sap(limit: 1, interactiveMaxWaitSeconds: 10);
        var first = sap.SendAsync(StockRead);
        await sap.WaitForArrivalsAsync(1);

        Task<HttpResponseMessage> person;
        using (SapRequestPriority.BeginInteractive())
        {
            person = sap.SendAsync(StockRead);
        }

        Assert.False(await sap.AnotherArrivesAsync());
        sap.AnswerOldest();
        await sap.WaitForArrivalsAsync(1);

        // It came in on the slot the first read gave back, so a third read has to wait for it.
        var third = sap.SendAsync(StockRead);
        Assert.False(await sap.AnotherArrivesAsync());

        sap.AnswerAll();
        using (await person) { }
        await sap.WaitForArrivalsAsync(1);
        sap.AnswerAll();
        using (await third) { }
        using (await first) { }

        Assert.Equal(1, sap.MostAtOnce);
        Assert.Equal(1, sap.Gate.Available);
    }

    [Fact]
    public async Task A_slot_is_given_back_when_SAP_fails_or_the_caller_gives_up_at_SAP()
    {
        var sap = new Sap(limit: 1);

        sap.FailNext = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => sap.SendAsync(StockRead));
        await sap.WaitForArrivalsAsync(1);

        using (var budget = new CancellationTokenSource())
        {
            var abandoned = sap.SendAsync(StockRead, cancellationToken: budget.Token);
            await sap.WaitForArrivalsAsync(1);
            budget.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        }

        var next = sap.SendAsync(StockRead);
        await sap.WaitForArrivalsAsync(1);
        sap.AnswerAll();
        using var response = await next;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, sap.Gate.Available);
    }

    [Theory]
    [InlineData("GET", "https://sap.invalid/b1s/v1/SQLQueries('STK_ITEMS_ABC123DEF456')")]
    [InlineData("POST", "https://sap.invalid/b1s/v1/SQLQueries('STK_ITEMS_ABC123DEF456')/List")]
    [InlineData("GET", "https://sap.invalid/b1s/v1/SQLQueries('WHS_BATCHES_VAN004')/List?$skip=0")]
    [InlineData("GET", "https://sap.invalid/b1s/v1/SQLQueries('DSK_INV_COST_RANGE')/List?$skip=0")]
    [InlineData("GET", "https://sap.invalid/b1s/v1/SQLQueries('RPT_STK_ITEMS')/List")]
    [InlineData("GET", "https://sap.invalid/b1s/v1/Invoices(123)")]
    public async Task Other_requests_are_not_limited(string method, string url)
    {
        var sap = new Sap(limit: 1);
        var stockRead = sap.SendAsync(StockRead);
        await sap.WaitForArrivalsAsync(1);

        var other = sap.SendAsync(url, new HttpMethod(method));
        await sap.WaitForArrivalsAsync(1);

        sap.AnswerAll();
        using (await other) { }
        using (await stockRead) { }
    }

    [Fact]
    public async Task A_limit_of_zero_turns_the_gate_off()
    {
        var sap = new Sap(limit: 0);
        var reads = Enumerable.Range(0, 5).Select(_ => sap.SendAsync(StockRead)).ToList();

        await sap.WaitForArrivalsAsync(5);
        sap.AnswerAll();
        foreach (var read in reads)
        {
            using (await read) { }
        }

        Assert.False(sap.Gate.Enabled);
        Assert.Equal(5, sap.MostAtOnce);
    }

    /// <summary>The gate over a scripted Service Layer that holds every request until told to answer.</summary>
    private sealed class Sap
    {
        private readonly Script _script = new();
        private readonly HttpMessageInvoker _invoker;

        public Sap(int limit, int interactiveMaxWaitSeconds = 10)
        {
            var settings = Options.Create(new SAPSettings
            {
                StockItemsMaxConcurrentReads = limit,
                StockItemsInteractiveMaxWaitSeconds = interactiveMaxWaitSeconds,
            });
            Gate = new SapStockItemsReadGate(settings, Clock);
            _invoker = new HttpMessageInvoker(
                new SAPStockItemsReadHandler(Gate, Clock, NullLogger<SAPStockItemsReadHandler>.Instance)
                {
                    InnerHandler = _script,
                });
        }

        public ManualClock Clock { get; } = new();
        public SapStockItemsReadGate Gate { get; }
        public int Sent => _script.Sent;
        public int MostAtOnce => _script.MostAtOnce;

        public bool FailNext
        {
            set => _script.FailNext = value;
        }

        public Task<HttpResponseMessage> SendAsync(string url, HttpMethod? method = null, CancellationToken cancellationToken = default) =>
            _invoker.SendAsync(new HttpRequestMessage(method ?? HttpMethod.Get, url), cancellationToken);

        public async Task WaitForArrivalsAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Assert.True(await _script.Arrived.WaitAsync(Patience), $"Only {i} of {count} expected reads reached SAP.");
            }
        }

        /// <summary>Whether another read reaches SAP within a short look; false is the expected answer.</summary>
        public Task<bool> AnotherArrivesAsync() => _script.Arrived.WaitAsync(TimeSpan.FromMilliseconds(300));

        public void AnswerOldest() => _script.AnswerOldest();

        public void AnswerAll() => _script.AnswerAll();
    }

    private sealed class Script : HttpMessageHandler
    {
        private readonly ConcurrentQueue<TaskCompletionSource> _waiting = new();
        private int _sent;
        private int _atOnce;
        private int _mostAtOnce;

        public SemaphoreSlim Arrived { get; } = new(0);
        public int Sent => Volatile.Read(ref _sent);
        public int MostAtOnce => Volatile.Read(ref _mostAtOnce);
        public bool FailNext { get; set; }

        public void AnswerOldest()
        {
            if (_waiting.TryDequeue(out var answer))
            {
                answer.TrySetResult();
            }
        }

        public void AnswerAll()
        {
            while (_waiting.TryDequeue(out var answer))
            {
                answer.TrySetResult();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sent);
            var atOnce = Interlocked.Increment(ref _atOnce);
            InterlockedMax(ref _mostAtOnce, atOnce);

            try
            {
                if (FailNext)
                {
                    FailNext = false;
                    Arrived.Release();
                    throw new HttpRequestException("SAP closed the connection.");
                }

                var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiting.Enqueue(answer);
                Arrived.Release();
                await answer.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            finally
            {
                Interlocked.Decrement(ref _atOnce);
            }
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
}
