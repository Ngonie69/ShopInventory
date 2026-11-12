using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Middleware;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins how many reads of each <c>SQLQueries</c> read family <see cref="SAPReadFamilyHandler"/> lets the
/// Service Layer work on at once, counting reads it is presumed to be still working on, and what happens
/// to a read that finds its family full.
/// </summary>
/// <remarks>
/// On 2026-11-12, 80 of 82 stuck reads began with no other stuck read of their family in flight from the
/// API: each was sent after the API gave up on the one before, while SAP carried on. So the slot a stuck
/// read held is kept for the stuck-read hold. The handlers run as in production, inside
/// <see cref="SAPStuckReadHandler"/>, over a scripted Service Layer and a manual clock. Each test has its
/// own gate, so they can run alongside each other.
/// </remarks>
public class SapReadFamilyGateTests
{
    private const string StkA = "https://sap.invalid/b1s/v1/SQLQueries('STK_ITEMS_AAAAAAAAAAAA')/List?$skip=0";
    private const string StkB = "https://sap.invalid/b1s/v1/SQLQueries('STK_ITEMS_BBBBBBBBBBBB')/List?$skip=0";
    private const string StkC = "https://sap.invalid/b1s/v1/SQLQueries('STK_ITEMS_CCCCCCCCCCCC')/List?$skip=0";
    private const string Van4 = "https://sap.invalid/b1s/v1/SQLQueries('WHS_BATCHES_VAN004')/List?$skip=0";
    private const string Van5 = "https://sap.invalid/b1s/v1/SQLQueries('WHS_BATCHES_VAN005')/List?$skip=0";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Hold = TimeSpan.FromMinutes(10);

    // ---- Which reads are limited ----------------------------------------------------------------

    [Theory]
    [InlineData("SQLQueries('STK_ITEMS_9F09D30404FF')/List?$skip=0", "STK_ITEMS")]
    [InlineData("SQLQueries('WHS_BATCHES_VAN004')/List?$skip=500", "WHS_BATCHES")]
    [InlineData("SQLQueries('WHS_BATCHES_KEFSHOP_ABC123DEF456')/List", "WHS_BATCHES")]
    [InlineData("SQLQueries('DSK_INV_COST_RANGE')/List?$skip=0", "DSK_INV_COST_RANGE")]
    [InlineData("SQLQueries('STOCK_QTY_KEFSHOP')/List", "STOCK_QTY")]
    [InlineData("SQLQueries('RPT_STK_ITEMS')/List", null)]
    [InlineData("SQLQueries('STK_ITEMSX')/List", null)]
    [InlineData("SQLQueries('NOBATCH_QTY_KEFSHOP')/List", null)]
    [InlineData("SQLQueries('PODCRA_RANGE')/List?$skip=0", null)]
    [InlineData("SQLQueries('ITEM_BATCHES_V1')/List", null)]
    [InlineData("SQLQueries('STK_ITEMS_9F09D30404FF')", null)]
    [InlineData("Invoices(123)", null)]
    public void A_read_belongs_to_the_family_its_query_code_names(string relative, string? family)
    {
        var gate = new SapReadFamilyGate(Options.Create(new SAPSettings()), TimeProvider.System);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://sap.invalid/b1s/v1/" + relative);

        var code = SapReadFamilyGate.QueryCodeOf(request);

        Assert.Equal(family, code is null ? null : gate.FamilyOf(code)?.Name);
    }

    [Fact]
    public void Only_a_GET_of_a_stored_querys_rows_is_a_read()
    {
        using var post = new HttpRequestMessage(HttpMethod.Post, StkA);
        Assert.Null(SapReadFamilyGate.QueryCodeOf(post));
    }

    [Fact]
    public void The_shipped_settings_limit_the_four_families_to_two_and_send_only_STK_ITEMS_when_full()
    {
        foreach (var settings in new[] { new SAPSettings(), ShippedSapSettings() })
        {
            var gate = new SapReadFamilyGate(Options.Create(settings), TimeProvider.System);

            Assert.Equal(
                ["DSK_INV_COST_RANGE", "STK_ITEMS", "STOCK_QTY", "WHS_BATCHES"],
                gate.Families.Select(family => family.Name).Order(StringComparer.Ordinal));
            Assert.All(gate.Families, family => Assert.Equal(2, family.Limit));
            Assert.Equal(["STK_ITEMS"], gate.Families.Where(family => family.SendInteractiveWhenFull).Select(family => family.Name));
            Assert.Equal(TimeSpan.FromSeconds(10), gate.InteractiveMaxWait);
            Assert.Equal(TimeSpan.FromSeconds(5), gate.BackgroundMaxWait);
        }
    }

    [Fact]
    public void A_family_is_changed_or_turned_off_from_an_environment_variable()
    {
        // The form web.config uses. Family names carry no trailing underscore so "__" still splits them.
        const string prefix = "SI_READ_FAMILY_TEST_";
        const string stkLimit = prefix + "SAP__ReadFamilies__STK_ITEMS__MaxConcurrentReads";
        const string vanLimit = prefix + "SAP__ReadFamilies__WHS_BATCHES__MaxConcurrentReads";
        Environment.SetEnvironmentVariable(stkLimit, "3");
        Environment.SetEnvironmentVariable(vanLimit, "0");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(ShippedAppSettingsPath())
                .AddEnvironmentVariables(prefix)
                .Build();
            var settings = configuration.GetSection("SAP").Get<SAPSettings>()!;
            var gate = new SapReadFamilyGate(Options.Create(settings), TimeProvider.System);

            var stk = gate.FamilyOf("STK_ITEMS_9F09D30404FF");
            Assert.NotNull(stk);
            Assert.Equal(3, stk.Limit);
            Assert.True(stk.SendInteractiveWhenFull);
            Assert.Null(gate.FamilyOf("WHS_BATCHES_VAN004"));
            Assert.Equal(2, gate.FamilyOf("STOCK_QTY_KEFSHOP")!.Limit);
        }
        finally
        {
            Environment.SetEnvironmentVariable(stkLimit, null);
            Environment.SetEnvironmentVariable(vanLimit, null);
        }
    }

    [Theory]
    [InlineData("GET", "https://sap.invalid/b1s/v1/SQLQueries('STK_ITEMS_ABC123DEF456')")]
    [InlineData("POST", "https://sap.invalid/b1s/v1/SQLQueries('STK_ITEMS_ABC123DEF456')/List")]
    [InlineData("GET", "https://sap.invalid/b1s/v1/SQLQueries('RPT_STK_ITEMS')/List")]
    [InlineData("GET", "https://sap.invalid/b1s/v1/SQLQueries('PODCRA_RANGE')/List?$skip=0")]
    [InlineData("GET", "https://sap.invalid/b1s/v1/SQLQueries('NOBATCH_QTY_KEFSHOP')/List")]
    [InlineData("GET", "https://sap.invalid/b1s/v1/Invoices(123)")]
    public async Task Other_requests_are_not_limited(string method, string url)
    {
        var sap = new Sap(limit: 1);
        var stockRead = sap.SendAsync(StkA);
        await sap.WaitForArrivalsAsync(1);

        var other = sap.SendAsync(url, new HttpMethod(method));
        await sap.WaitForArrivalsAsync(1);

        sap.AnswerAll();
        using (await other) { }
        using (await stockRead) { }
    }

    [Fact]
    public async Task No_families_turns_the_gate_off()
    {
        var sap = new Sap(families: []);
        var reads = Enumerable.Range(0, 5).Select(_ => sap.SendAsync(StkA)).ToList();

        await sap.WaitForArrivalsAsync(5);
        sap.AnswerAll();
        foreach (var read in reads)
        {
            using (await read) { }
        }

        Assert.False(sap.Gate.Enabled);
        Assert.Equal(5, sap.MostAtOnce);
    }

    [Fact]
    public async Task A_limit_of_zero_turns_that_family_off()
    {
        var sap = new Sap(families: new()
        {
            ["STK_ITEMS"] = new() { MaxConcurrentReads = 0 },
            ["WHS_BATCHES"] = new() { MaxConcurrentReads = 1 },
        });
        var reads = Enumerable.Range(0, 3).Select(_ => sap.SendAsync(StkA)).ToList();

        await sap.WaitForArrivalsAsync(3);
        sap.AnswerAll();
        foreach (var read in reads)
        {
            using (await read) { }
        }

        Assert.Null(sap.Gate.FamilyOf("STK_ITEMS_AAAAAAAAAAAA"));
        Assert.NotNull(sap.Gate.FamilyOf("WHS_BATCHES_VAN004"));
    }

    // ---- In flight -------------------------------------------------------------------------------

    [Fact]
    public async Task No_more_than_the_limit_of_a_family_reach_SAP_at_once()
    {
        var sap = new Sap(limit: 2);
        var reads = new[] { StkA, StkB, StkC, StkA }.Select(url => sap.SendAsync(url)).ToList();

        await sap.WaitForArrivalsAsync(2);
        Assert.False(await sap.AnotherArrivesAsync(), "A third STK_ITEMS read reached SAP while two were running.");

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

        Assert.Equal(4, sap.Sent);
        Assert.Equal(2, sap.MostAtOnce);
        Assert.Equal(2, sap.Family("STK_ITEMS").Available);
    }

    [Fact]
    public async Task Each_family_has_its_own_slots()
    {
        var sap = new Sap(limit: 1);
        var stock = sap.SendAsync(StkA);
        await sap.WaitForArrivalsAsync(1);

        var van = sap.SendAsync(Van4);
        await sap.WaitForArrivalsAsync(1);

        var secondStock = sap.SendAsync(StkB);
        Assert.False(await sap.AnotherArrivesAsync(), "A second STK_ITEMS read reached SAP past a limit of one.");

        sap.AnswerAll();
        await sap.WaitForArrivalsAsync(1);
        sap.AnswerAll();
        using (await stock) { }
        using (await van) { }
        using (await secondStock) { }
        Assert.Equal(2, sap.MostAtOnce);
    }

    [Fact]
    public async Task A_background_read_waits_briefly_then_is_refused_without_reaching_SAP()
    {
        var sap = new Sap(limit: 1);
        var first = sap.SendAsync(StkA);
        await sap.WaitForArrivalsAsync(1);

        var queued = sap.SendAsync(StkB);
        sap.Clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(await sap.AnotherArrivesAsync());
        Assert.False(queued.IsCompleted, "The background read gave up before its five seconds.");

        sap.Clock.Advance(TimeSpan.FromSeconds(1));
        var ex = await Assert.ThrowsAsync<SapReadFamilyFullException>(() => queued);

        // Callers already handle a stock read that timed out; this one reaches them as the same kind,
        // and the client's transient retry leaves it alone.
        Assert.IsAssignableFrom<TimeoutException>(ex);
        Assert.IsAssignableFrom<SapReadNotSentException>(ex);
        Assert.Equal("STK_ITEMS", ex.Family);
        Assert.Null(ex.FirstSlotFreesAt);

        sap.AnswerAll();
        using (await first) { }
        Assert.Equal(1, sap.Sent);
        Assert.Equal(1, sap.Family("STK_ITEMS").Available);
    }

    [Fact]
    public async Task A_background_read_takes_a_slot_that_frees_within_its_wait()
    {
        var sap = new Sap(limit: 1);
        var first = sap.SendAsync(StkA);
        await sap.WaitForArrivalsAsync(1);

        var queued = sap.SendAsync(StkB);
        sap.Clock.Advance(TimeSpan.FromSeconds(4));
        sap.AnswerOldest();
        await sap.WaitForArrivalsAsync(1);
        sap.AnswerAll();

        using (var answered = await queued)
        {
            Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        }

        using (await first) { }
        Assert.Equal(1, sap.MostAtOnce);
        Assert.Equal(1, sap.Family("STK_ITEMS").Available);
    }

    [Fact]
    public async Task A_read_whose_caller_gives_up_while_it_waits_takes_no_slot()
    {
        var sap = new Sap(limit: 1);
        var first = sap.SendAsync(StkA);
        await sap.WaitForArrivalsAsync(1);

        using var budget = new CancellationTokenSource();
        var queued = sap.SendAsync(StkB, cancellationToken: budget.Token);
        budget.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        sap.AnswerAll();
        using (await first) { }

        var next = sap.SendAsync(StkC);
        await sap.WaitForArrivalsAsync(1);
        sap.AnswerAll();
        using (await next) { }
        Assert.Equal(2, sap.Sent);
        Assert.Equal(1, sap.Family("STK_ITEMS").Available);
    }

    [Fact]
    public async Task A_persons_STK_ITEMS_read_is_sent_anyway_once_it_has_waited_the_maximum()
    {
        // The invoice stock guard reads STK_ITEMS and fails closed: refused, the invoice would be too.
        var sap = new Sap(limit: 1);
        var inFlight = sap.SendAsync(StkA);
        await sap.WaitForArrivalsAsync(1);

        Task<HttpResponseMessage> person;
        using (SapRequestPriority.BeginInteractive())
        {
            person = sap.SendAsync(StkB);
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

        using (await inFlight) { }

        // Sent without a slot, it gives none back: the limit is still exactly one.
        Assert.Equal(1, sap.Family("STK_ITEMS").Available);
    }

    [Fact]
    public async Task A_persons_read_of_another_family_is_refused_once_it_has_waited_the_maximum()
    {
        // 34 of the 82 stuck reads on 2026-11-12 were van handsets loading their catalogue (WHS_BATCHES_VAN*).
        var sap = new Sap(limit: 1);
        var inFlight = sap.SendAsync(Van4);
        await sap.WaitForArrivalsAsync(1);

        Task<HttpResponseMessage> person;
        using (SapRequestPriority.BeginInteractive())
        {
            person = sap.SendAsync(Van5);
        }

        sap.Clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(await sap.AnotherArrivesAsync());
        Assert.False(person.IsCompleted, "The person's read was refused before its maximum wait.");

        sap.Clock.Advance(TimeSpan.FromSeconds(1));
        var ex = await Assert.ThrowsAsync<SapReadFamilyFullException>(() => person);
        Assert.Equal("WHS_BATCHES", ex.Family);

        sap.AnswerAll();
        using (await inFlight) { }
        Assert.Equal(1, sap.Sent);
    }

    [Fact]
    public async Task A_persons_read_takes_a_slot_that_frees_up_in_time()
    {
        var sap = new Sap(limit: 1);
        var first = sap.SendAsync(Van4);
        await sap.WaitForArrivalsAsync(1);

        Task<HttpResponseMessage> person;
        using (SapRequestPriority.BeginInteractive())
        {
            person = sap.SendAsync(Van5);
        }

        Assert.False(await sap.AnotherArrivesAsync());
        sap.Clock.Advance(TimeSpan.FromSeconds(3));
        sap.AnswerOldest();
        await sap.WaitForArrivalsAsync(1);
        sap.AnswerAll();

        using (var answered = await person)
        {
            Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        }

        using (await first) { }
        Assert.Equal(1, sap.MostAtOnce);
        Assert.Equal(1, sap.Family("WHS_BATCHES").Available);
    }

    [Fact]
    public async Task A_slot_is_given_back_when_SAP_fails_or_the_caller_gives_up_before_SAP_had_it_long()
    {
        var sap = new Sap(limit: 1);

        sap.FailNext = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => sap.SendAsync(StkA));
        await sap.WaitForArrivalsAsync(1);

        // A closed page after five seconds says nothing about SAP: nothing is presumed left running.
        await sap.HangThenCancelAsync(StkB, TimeSpan.FromSeconds(5));

        Assert.Equal(1, sap.Family("STK_ITEMS").Available);
        Assert.Equal(0, sap.Family("STK_ITEMS").Kept);
    }

    // ---- Stuck reads keep their slot -------------------------------------------------------------

    [Fact]
    public async Task A_read_SAP_is_presumed_still_working_on_keeps_its_slot_for_the_hold()
    {
        var sap = new Sap(limit: 1);
        var stk = sap.Family("STK_ITEMS");
        await sap.HangThenCancelAsync(StkA, TimeSpan.FromSeconds(60));
        var gaveUpAt = sap.Clock.GetUtcNow();

        Assert.Equal(1, stk.Kept);
        Assert.Equal(0, stk.Available);

        // Every slot is kept, so another read of the family is refused at once rather than after a wait
        // nothing can end.
        var ex = await Assert.ThrowsAsync<SapReadFamilyFullException>(() => sap.SendAsync(StkB));
        Assert.Equal(gaveUpAt + Hold, ex.FirstSlotFreesAt);

        sap.Clock.Advance(Hold - TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<SapReadFamilyFullException>(() => sap.SendAsync(StkB));

        sap.Clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => stk.Available == 1, "The kept slot never came back when the hold ended.");
        Assert.Equal(0, stk.Kept);

        var next = sap.SendAsync(StkB);
        await sap.WaitForArrivalsAsync(1);
        sap.AnswerAll();
        using var response = await next;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, sap.Sent);
        Assert.Equal(0, sap.Clock.ActiveTimers);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_read_the_load_balancer_gave_up_on_keeps_its_slot(HttpStatusCode status)
    {
        // The load balancer answers 502 at its 300 s ProxyTimeout while the node carries on.
        var sap = new Sap(limit: 1);
        var read = sap.SendAsync(Van4);
        await sap.WaitForArrivalsAsync(1);
        sap.Clock.Advance(TimeSpan.FromSeconds(20));
        sap.AnswerOldest(status);

        using (var response = await read)
        {
            Assert.Equal(status, response.StatusCode);
        }

        Assert.Equal(1, sap.Family("WHS_BATCHES").Kept);
        await Assert.ThrowsAsync<SapReadFamilyFullException>(() => sap.SendAsync(Van5));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, 20)]
    [InlineData(HttpStatusCode.BadGateway, 5)]
    public async Task Any_other_answer_gives_the_slot_back(HttpStatusCode status, int secondsAtSap)
    {
        var sap = new Sap(limit: 1);
        var read = sap.SendAsync(Van4);
        await sap.WaitForArrivalsAsync(1);
        sap.Clock.Advance(TimeSpan.FromSeconds(secondsAtSap));
        sap.AnswerOldest(status);
        using (await read) { }

        Assert.Equal(0, sap.Family("WHS_BATCHES").Kept);
        Assert.Equal(1, sap.Family("WHS_BATCHES").Available);
    }

    [Fact]
    public async Task SAP_answering_the_same_read_gives_its_kept_slot_back()
    {
        var sap = new Sap(limit: 2);
        var stk = sap.Family("STK_ITEMS");
        await sap.HangThenCancelAsync(StkA, TimeSpan.FromSeconds(60));
        Assert.Equal(1, stk.Kept);

        // A person's read of the same URL is not held, and SAP answering it ends the presumption.
        using (SapRequestPriority.BeginInteractive())
        {
            var again = sap.SendAsync(StkA);
            await sap.WaitForArrivalsAsync(1);
            sap.AnswerAll();
            using var response = await again;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(0, stk.Kept);
        Assert.Equal(2, stk.Available);
        Assert.Equal(0, sap.Clock.ActiveTimers);
    }

    [Fact]
    public async Task A_background_read_SAP_is_still_working_on_is_refused_by_the_hold_before_it_looks_for_a_slot()
    {
        var sap = new Sap(limit: 2);
        await sap.HangThenCancelAsync(StkA, TimeSpan.FromSeconds(60));

        await Assert.ThrowsAsync<SapReadStillRunningException>(() => sap.SendAsync(StkA));
        Assert.Equal(1, sap.Family("STK_ITEMS").Available);
    }

    [Fact]
    public async Task When_every_slot_is_kept_a_persons_STK_ITEMS_read_goes_at_once_and_takes_nothing_it_cannot_give_back()
    {
        var sap = new Sap(limit: 1);
        var stk = sap.Family("STK_ITEMS");
        await sap.HangThenCancelAsync(StkA, TimeSpan.FromSeconds(60));

        // No clock movement: it does not wait ten seconds for a slot that frees in ten minutes.
        using (SapRequestPriority.BeginInteractive())
        {
            await sap.HangThenCancelAsync(StkB, TimeSpan.FromSeconds(60));
        }

        // It got stuck too, but went without a slot and none was free, so nothing more is kept, and
        // when the first slot comes back the family has exactly its one slot.
        Assert.Equal(1, stk.Kept);
        sap.Clock.Advance(Hold);
        await EventuallyAsync(() => stk.Available == 1, "The kept slot never came back when the hold ended.");
        Assert.Equal(0, stk.Kept);
        Assert.False(await SlotWaitingAsync(stk), "More slots came back than the family has.");
    }

    [Fact]
    public async Task A_person_refused_while_every_slot_is_kept_is_refused_at_once()
    {
        var sap = new Sap(limit: 1);
        await sap.HangThenCancelAsync(Van4, TimeSpan.FromSeconds(60));

        using (SapRequestPriority.BeginInteractive())
        {
            await Assert.ThrowsAsync<SapReadFamilyFullException>(() => sap.SendAsync(Van5));
        }

        Assert.Equal(1, sap.Sent);
    }

    [Fact]
    public async Task With_the_stuck_read_hold_off_a_stuck_read_gives_its_slot_back()
    {
        var sap = new Sap(limit: 1, holdMinutes: 0);
        await sap.HangThenCancelAsync(StkA, TimeSpan.FromSeconds(60));

        Assert.Equal(0, sap.Family("STK_ITEMS").Kept);
        Assert.Equal(1, sap.Family("STK_ITEMS").Available);
    }

    // ---- Harness ---------------------------------------------------------------------------------

    private static async Task EventuallyAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, message);
            await Task.Delay(10);
        }
    }

    /// <summary>Whether the family has a second slot to give: it must not, with a limit of one.</summary>
    private static async Task<bool> SlotWaitingAsync(SapReadFamily family)
    {
        if (!family.TryEnter())
        {
            return false;
        }

        try
        {
            await Task.Yield();
            return family.TryEnter();
        }
        finally
        {
            family.Exit();
        }
    }

    private static string ShippedAppSettingsPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopInventory", "appsettings.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "ShopInventory", "appsettings.json");
    }

    /// <summary>The SAP section exactly as the API ships it.</summary>
    private static SAPSettings ShippedSapSettings() =>
        new ConfigurationBuilder()
            .AddJsonFile(ShippedAppSettingsPath())
            .Build()
            .GetSection("SAP")
            .Get<SAPSettings>()!;

    /// <summary>
    /// The stuck-read and read-family handlers, in production order, over a scripted Service Layer that
    /// holds every request until told to answer.
    /// </summary>
    private sealed class Sap
    {
        private readonly Script _script;
        private readonly HttpMessageInvoker _invoker;

        public Sap(int limit = 1, int holdMinutes = 10, Dictionary<string, SapReadFamilySettings>? families = null)
        {
            var settings = Options.Create(new SAPSettings
            {
                ReadFamilies = families ?? new Dictionary<string, SapReadFamilySettings>
                {
                    ["STK_ITEMS"] = new() { MaxConcurrentReads = limit, SendInteractiveWhenFull = true },
                    ["WHS_BATCHES"] = new() { MaxConcurrentReads = limit },
                    ["DSK_INV_COST_RANGE"] = new() { MaxConcurrentReads = limit },
                    ["STOCK_QTY"] = new() { MaxConcurrentReads = limit },
                },
                ReadFamilyInteractiveMaxWaitSeconds = 10,
                ReadFamilyBackgroundMaxWaitSeconds = 5,
                StuckReadHoldMinutes = holdMinutes,
            });

            StuckReads = new SapStuckReadState(settings, Clock);
            Gate = new SapReadFamilyGate(settings, Clock);
            _script = new Script(Clock);
            _invoker = new HttpMessageInvoker(
                new SAPStuckReadHandler(StuckReads, NullLogger<SAPStuckReadHandler>.Instance)
                {
                    InnerHandler = new SAPReadFamilyHandler(Gate, StuckReads, Clock, NullLogger<SAPReadFamilyHandler>.Instance)
                    {
                        InnerHandler = _script,
                    },
                });
        }

        public ManualClock Clock { get; } = new();
        public SapStuckReadState StuckReads { get; }
        public SapReadFamilyGate Gate { get; }
        public int Sent => _script.Sent;
        public int MostAtOnce => _script.MostAtOnce;

        public bool FailNext
        {
            set => _script.FailNext = value;
        }

        public SapReadFamily Family(string name) => Gate.Families.Single(family => family.Name == name);

        /// <remarks>
        /// Bounded by <see cref="Patience"/>, so a read the gate should have refused, but sent to a SAP that
        /// never answers unprompted, fails the test with a <see cref="TimeoutException"/> instead of hanging it.
        /// </remarks>
        public Task<HttpResponseMessage> SendAsync(string url, HttpMethod? method = null, CancellationToken cancellationToken = default) =>
            _invoker.SendAsync(new HttpRequestMessage(method ?? HttpMethod.Get, url), cancellationToken).WaitAsync(Patience);

        public async Task WaitForArrivalsAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Assert.True(await _script.Arrived.WaitAsync(Patience), $"Only {i} of {count} expected reads reached SAP.");
            }
        }

        /// <summary>Whether another read reaches SAP within a short look; false is the expected answer.</summary>
        public Task<bool> AnotherArrivesAsync() => _script.Arrived.WaitAsync(TimeSpan.FromMilliseconds(300));

        /// <summary>SAP has the read for <paramref name="atSap"/> without answering, then its caller gives up.</summary>
        public async Task HangThenCancelAsync(string url, TimeSpan atSap)
        {
            using var budget = new CancellationTokenSource();
            var send = SendAsync(url, cancellationToken: budget.Token);
            await WaitForArrivalsAsync(1);
            Clock.Advance(atSap);
            budget.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        }

        public void AnswerOldest(HttpStatusCode status = HttpStatusCode.OK) => _script.AnswerOldest(status);

        public void AnswerAll() => _script.AnswerAll();
    }

    /// <summary>
    /// A Service Layer that marks each request as reached, the way <see cref="SAPConcurrencyHandler"/> does,
    /// and holds it until answered or cancelled.
    /// </summary>
    private sealed class Script(ManualClock clock) : HttpMessageHandler
    {
        private readonly object _sync = new();
        private readonly List<TaskCompletionSource<HttpStatusCode>> _waiting = [];
        private int _sent;
        private int _atOnce;
        private int _mostAtOnce;

        public SemaphoreSlim Arrived { get; } = new(0);
        public int Sent => Volatile.Read(ref _sent);
        public int MostAtOnce => Volatile.Read(ref _mostAtOnce);
        public bool FailNext { get; set; }

        public void AnswerOldest(HttpStatusCode status)
        {
            TaskCompletionSource<HttpStatusCode>? oldest;
            lock (_sync)
            {
                oldest = _waiting.FirstOrDefault();
                if (oldest is not null)
                {
                    _waiting.RemoveAt(0);
                }
            }

            oldest?.TrySetResult(status);
        }

        public void AnswerAll()
        {
            TaskCompletionSource<HttpStatusCode>[] all;
            lock (_sync)
            {
                all = [.. _waiting];
                _waiting.Clear();
            }

            foreach (var answer in all)
            {
                answer.TrySetResult(HttpStatusCode.OK);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sent);
            var atOnce = Interlocked.Increment(ref _atOnce);
            InterlockedMax(ref _mostAtOnce, atOnce);
            SapRequestMarks.MarkReachedSap(request, clock.GetUtcNow());

            var answer = new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                if (FailNext)
                {
                    FailNext = false;
                    Arrived.Release();
                    throw new HttpRequestException("SAP closed the connection.");
                }

                lock (_sync)
                {
                    _waiting.Add(answer);
                }

                Arrived.Release();
                var status = await answer.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(status);
            }
            finally
            {
                lock (_sync)
                {
                    _waiting.Remove(answer);
                }

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
