using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Middleware;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins when <see cref="SAPStuckReadHandler"/> holds a SQLQueries read back, and when it never does.
/// </summary>
/// <remarks>
/// A Service Layer process that gets stuck on one of these reads keeps working on it, at full CPU and
/// up to ~8 GB, for minutes to hours after this client gave up. Sending the same read again meanwhile
/// is how a second process got stuck beside it: on KFLDB on 2026-11-10/11, 110 of the 379 repeats of a
/// slow read got stuck too.
/// </remarks>
public class SapStuckReadTests
{
    private const string StockRead = "https://sap.invalid/b1s/v1/SQLQueries('STK_ITEMS_ABC123DEF456')/List?$skip=0";

    [Fact]
    public async Task A_background_read_SAP_took_and_never_answered_is_not_sent_again()
    {
        var sap = new Sap();
        await sap.HangThenCancelAsync(StockRead, TimeSpan.FromSeconds(60));
        var cancelledAt = sap.Clock.GetUtcNow();

        var ex = await Assert.ThrowsAsync<SapReadStillRunningException>(() => sap.SendAsync(StockRead));

        Assert.Equal(1, sap.Sent);
        Assert.Equal(cancelledAt + TimeSpan.FromMinutes(10), ex.HeldUntil);
        // Callers already handle a stock read that timed out; a held one reaches them as the same kind.
        Assert.IsAssignableFrom<TimeoutException>(ex);
    }

    [Fact]
    public async Task The_hold_ends_when_its_time_is_up()
    {
        var sap = new Sap();
        await sap.HangThenCancelAsync(StockRead, TimeSpan.FromSeconds(60));

        sap.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<SapReadStillRunningException>(() => sap.SendAsync(StockRead));

        sap.Clock.Advance(TimeSpan.FromSeconds(1));
        using var response = await sap.SendAsync(StockRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, sap.Sent);
    }

    [Fact]
    public async Task A_read_a_person_is_waiting_on_is_always_sent_and_its_answer_ends_the_hold()
    {
        // The invoice stock guard fails closed, so refusing a person's read would stop invoicing for
        // the whole hold — while a fresh attempt usually lands on a Service Layer process that is fine.
        var sap = new Sap();
        await sap.HangThenCancelAsync(StockRead, TimeSpan.FromSeconds(60));

        using (SapRequestPriority.BeginInteractive())
        {
            using var answered = await sap.SendAsync(StockRead);
            Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        }

        using var background = await sap.SendAsync(StockRead);
        Assert.Equal(HttpStatusCode.OK, background.StatusCode);
        Assert.Equal(3, sap.Sent);
    }

    /// <summary>
    /// A read that only feeds a screen with an older answer to show is held whoever is waiting on it: on
    /// 2026-11-12, 23 of the 65 stuck van catalogue reads were repeats sent inside the hold.
    /// </summary>
    [Fact]
    public async Task A_display_only_read_a_person_is_waiting_on_is_held_too()
    {
        var sap = new Sap();
        await sap.HangThenCancelAsync(StockRead, TimeSpan.FromSeconds(60));

        using (SapRequestPriority.BeginInteractive())
        {
            await Assert.ThrowsAsync<SapReadStillRunningException>(() => sap.SendAsync(StockRead, displayOnly: true));

            // The same read unmarked, as the stock guard and transfer allocation send it, still goes.
            using var guard = await sap.SendAsync(StockRead);
            Assert.Equal(HttpStatusCode.OK, guard.StatusCode);
        }

        Assert.Equal(2, sap.Sent);
    }

    [Fact]
    public async Task A_display_only_read_is_sent_when_nothing_is_held()
    {
        var sap = new Sap();

        using (SapRequestPriority.BeginInteractive())
        {
            using var response = await sap.SendAsync(StockRead, displayOnly: true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(1, sap.Sent);
    }

    [Fact]
    public async Task A_read_a_person_was_waiting_on_that_got_stuck_holds_background_reads_back()
    {
        var sap = new Sap();
        using (SapRequestPriority.BeginInteractive())
        {
            await sap.HangThenCancelAsync(StockRead, TimeSpan.FromSeconds(60));
        }

        await Assert.ThrowsAsync<SapReadStillRunningException>(() => sap.SendAsync(StockRead));
        Assert.Equal(1, sap.Sent);
    }

    [Theory]
    [InlineData("https://sap.invalid/b1s/v1/SQLQueries('STK_ITEMS_ABC123DEF456')/List?$skip=500")]
    [InlineData("https://sap.invalid/b1s/v1/SQLQueries('STK_ITEMS_FEDCBA987654')/List?$skip=0")]
    [InlineData("https://sap.invalid/b1s/v1/SQLQueries('DSK_INV_COST_RANGE')/List?docEntryStart=1&$skip=0")]
    public async Task Only_the_identical_read_is_held(string other)
    {
        var sap = new Sap();
        await sap.HangThenCancelAsync(StockRead, TimeSpan.FromSeconds(60));

        using var response = await sap.SendAsync(other);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_read_given_up_on_soon_after_it_reached_SAP_is_not_held()
    {
        // A person closing a page a few seconds in says nothing about SAP being stuck.
        var sap = new Sap();
        await sap.HangThenCancelAsync(StockRead, TimeSpan.FromSeconds(14));

        using var response = await sap.SendAsync(StockRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_read_that_timed_out_still_queued_for_a_slot_is_not_held()
    {
        // It never reached SAP, so there is nothing running there to wait for.
        var sap = new Sap();
        await sap.HangThenCancelAsync(StockRead, TimeSpan.FromSeconds(60), reachSap: false);

        using var response = await sap.SendAsync(StockRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task The_load_balancer_giving_up_after_a_long_wait_holds_the_read(HttpStatusCode status)
    {
        // The balancer answers 502 at its 300 s ProxyTimeout while the node carries on; the client's
        // transient retry would otherwise send the read straight back.
        var sap = new Sap();
        using (var gaveUp = await sap.AnswerAfterAsync(StockRead, status, TimeSpan.FromSeconds(300)))
        {
            Assert.Equal(status, gaveUp.StatusCode);
        }

        await Assert.ThrowsAsync<SapReadStillRunningException>(() => sap.SendAsync(StockRead));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, 2)]
    [InlineData(HttpStatusCode.OK, 50)]
    [InlineData(HttpStatusCode.InternalServerError, 300)]
    public async Task An_answer_SAP_did_give_is_not_held(HttpStatusCode status, int secondsAtSap)
    {
        var sap = new Sap();
        using (await sap.AnswerAfterAsync(StockRead, status, TimeSpan.FromSeconds(secondsAtSap)))
        {
        }

        using var response = await sap.SendAsync(StockRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "https://sap.invalid/b1s/v1/Invoices(123)")]
    [InlineData("GET", "https://sap.invalid/b1s/v1/SQLQueries('STK_ITEMS_ABC123DEF456')")]
    [InlineData("POST", "https://sap.invalid/b1s/v1/SQLQueries")]
    [InlineData("POST", "https://sap.invalid/b1s/v1/SQLQueries('STK_ITEMS_ABC123DEF456')/List")]
    public async Task Requests_other_than_a_stored_query_read_are_never_held(string method, string url)
    {
        var sap = new Sap();
        var verb = new HttpMethod(method);
        await sap.HangThenCancelAsync(url, TimeSpan.FromSeconds(60), method: verb);

        using var response = await sap.SendAsync(url, verb);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_hold_of_zero_minutes_turns_the_guard_off()
    {
        var sap = new Sap(holdMinutes: 0);
        await sap.HangThenCancelAsync(StockRead, TimeSpan.FromSeconds(60));

        using var response = await sap.SendAsync(StockRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The guard over a scripted Service Layer that marks each request as having reached SAP, the
    /// way SAPConcurrencyHandler does once the request holds a slot.
    /// </summary>
    private sealed class Sap
    {
        private readonly Script _script;
        private readonly HttpMessageInvoker _invoker;

        public Sap(int holdMinutes = 10)
        {
            State = new SapStuckReadState(Options.Create(new SAPSettings { StuckReadHoldMinutes = holdMinutes }), Clock);
            _script = new Script(Clock);
            _invoker = new HttpMessageInvoker(
                new SAPStuckReadHandler(State, NullLogger<SAPStuckReadHandler>.Instance) { InnerHandler = _script });
        }

        public ManualClock Clock { get; } = new();
        public SapStuckReadState State { get; }
        public int Sent => _script.Sent;

        public Task<HttpResponseMessage> SendAsync(
            string url,
            HttpMethod? method = null,
            CancellationToken cancellationToken = default,
            bool displayOnly = false)
        {
            var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
            if (displayOnly)
            {
                SapRequestMarks.MarkDisplayOnly(request);
            }

            return _invoker.SendAsync(request, cancellationToken);
        }

        /// <summary>SAP has the request for <paramref name="atSap"/> without answering, then the caller's budget cancels it.</summary>
        public async Task HangThenCancelAsync(string url, TimeSpan atSap, bool reachSap = true, HttpMethod? method = null)
        {
            using var budget = new CancellationTokenSource();
            _script.Next = new Step(Hang: true, ReachSap: reachSap, HttpStatusCode.OK, TimeSpan.Zero);
            var send = SendAsync(url, method, budget.Token);
            Assert.True(await _script.Hanging.WaitAsync(TimeSpan.FromSeconds(30)), "The read never reached the scripted SAP.");
            Clock.Advance(atSap);
            budget.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        }

        /// <summary>SAP has the request for <paramref name="atSap"/>, then answers <paramref name="status"/>.</summary>
        public Task<HttpResponseMessage> AnswerAfterAsync(string url, HttpStatusCode status, TimeSpan atSap)
        {
            _script.Next = new Step(Hang: false, ReachSap: true, status, atSap);
            return SendAsync(url);
        }
    }

    private sealed record Step(bool Hang, bool ReachSap, HttpStatusCode Status, TimeSpan AtSap);

    private sealed class Script(ManualClock clock) : HttpMessageHandler
    {
        private static readonly Step Answer = new(Hang: false, ReachSap: true, HttpStatusCode.OK, TimeSpan.Zero);
        private int _sent;

        public Step Next { get; set; } = Answer;
        public SemaphoreSlim Hanging { get; } = new(0);
        public int Sent => Volatile.Read(ref _sent);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sent);
            var step = Next;
            Next = Answer;

            if (step.ReachSap)
            {
                SapRequestMarks.MarkReachedSap(request, clock.GetUtcNow());
            }

            if (step.Hang)
            {
                Hanging.Release();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            clock.Advance(step.AtSap);
            return new HttpResponseMessage(step.Status);
        }
    }
}
