using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ApiSchemes = ShopInventory.Authentication.AuthenticationSchemes;
using ShopInventory.Configuration;
using ShopInventory.Features.CallTimings.Queries.GetCallTimings;
using ShopInventory.Middleware;
using ShopInventory.Services;
using ShopInventory.Services.CallTimings;

namespace ShopInventory.Tests;

/// <summary>
/// Pins what the call timings recorder keeps: the label of each SAP call (never a key or a query string),
/// how a call's outcome and waits are read off the real SAP pipeline, and what an inbound request is
/// kept as (its route template and kind of caller, never its path).
/// </summary>
/// <remarks>
/// The SAP calls go through <see cref="SAPCallTimingHandler"/>, <see cref="SAPReadFamilyHandler"/> and
/// <see cref="SAPConcurrencyHandler"/> as in production, over a scripted Service Layer and a manual
/// clock. Each test has its own recorder and family gate.
/// </remarks>
public class CallTimingRecordingTests
{
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(100);

    // ---- The ring ---------------------------------------------------------------------------------

    [Fact]
    public void A_full_ring_drops_its_oldest_and_says_so()
    {
        var ring = new TimingRing<int>(3);
        Assert.Null(ring.Oldest());

        foreach (var value in new[] { 1, 2, 3, 4, 5 })
        {
            ring.Add(value);
        }

        var (items, dropped) = ring.Snapshot(_ => true);
        Assert.Equal([3, 4, 5], items);
        Assert.True(dropped);
        Assert.Equal(3, ring.Oldest());
        Assert.Equal(5, ring.Added);
        Assert.Equal([4], ring.Snapshot(value => value % 2 == 0).Items);
    }

    [Fact]
    public void A_ring_that_has_not_filled_dropped_nothing()
    {
        var ring = new TimingRing<int>(3);
        ring.Add(7);
        ring.Add(8);

        var (items, dropped) = ring.Snapshot(_ => true);
        Assert.Equal([7, 8], items);
        Assert.False(dropped);
        Assert.Equal(7, ring.Oldest());
    }

    // ---- SAP call labels --------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "SQLQueries('STK_ITEMS_9F09D30404FF')/List?$skip=0", "STK_ITEMS", "STK_ITEMS_9F09D30404FF")]
    [InlineData("GET", "SQLQueries('WHS_BATCHES_VAN004')/List?$skip=500", "WHS_BATCHES", "WHS_BATCHES_VAN004")]
    [InlineData("GET", "SQLQueries('PODCRA_RANGE')/List?$skip=0", "PODCRA_RANGE", "PODCRA_RANGE")]
    [InlineData("GET", "SQLQueries('VAN_SALE_ITEMS_0123456789AB')/List", "VAN_SALE_ITEMS", "VAN_SALE_ITEMS_0123456789AB")]
    [InlineData("GET", "SQLQueries('NOBATCH_QTY_KEFSHOP')/List", "NOBATCH_QTY_KEFSHOP", "NOBATCH_QTY_KEFSHOP")]
    [InlineData("GET", "SQLQueries('STK_ITEMS_9F09D30404FF')?$select=SqlText", "GET SQLQueries", "GET SQLQueries")]
    [InlineData("PATCH", "SQLQueries('STK_ITEMS_9F09D30404FF')", "PATCH SQLQueries", "PATCH SQLQueries")]
    [InlineData("GET", "BusinessPartners('CUST0001')?$select=CardCode,CurrentAccountBalance", "GET BusinessPartners", "GET BusinessPartners")]
    [InlineData("POST", "Drafts(1234)/SaveDraftToDocument", "POST Drafts/SaveDraftToDocument", "POST Drafts/SaveDraftToDocument")]
    [InlineData("GET", "Items('A(1)B')/ItemWarehouseInfoCollection", "GET Items/ItemWarehouseInfoCollection", "GET Items/ItemWarehouseInfoCollection")]
    [InlineData("GET", "Invoices?$filter=CardCode eq 'CUST0001'", "GET Invoices", "GET Invoices")]
    [InlineData("POST", "Login", "POST Login", "POST Login")]
    public void A_call_is_labelled_without_its_keys_or_query_string(string method, string relative, string family, string operation)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://sap.invalid:50000/b1s/v1/" + relative);

        var labels = SapCallLabels.Of(request, Gate());

        Assert.Equal((family, operation), labels);
        Assert.DoesNotContain("CUST0001", labels.Family + labels.Operation);
    }

    // ---- The SAP pipeline -------------------------------------------------------------------------

    [Fact]
    public async Task An_answered_read_is_kept_with_its_family_time_at_sap_and_the_route_that_asked()
    {
        var clock = new ManualClock();
        var (pipeline, recorder, _) = Pipeline(clock, (_, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(840));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var inbound = new ApiCallContext("GET api/Stock/{warehouseCode}");
        using (ApiCallContext.Begin(inbound))
        {
            using var response = await Send(pipeline, Stk);
        }

        var call = Assert.Single(recorder.SapCalls.Snapshot(_ => true).Items);
        Assert.Equal(("STK_ITEMS", "STK_ITEMS_AAAAAAAAAAAA", SapCallOutcome.Ok, (short)200), (call.Family, call.Operation, call.Outcome, call.StatusCode));
        Assert.Equal((840, 840), (call.DurationMs, call.AtSapMs));
        Assert.Equal("GET api/Stock/{warehouseCode}", call.Origin);
        Assert.False(call.PresumedStuck);
        Assert.Equal((1, 840), (inbound.SapCalls, inbound.SapMs));
        Assert.Empty(recorder.InFlight);
    }

    [Fact]
    public async Task A_call_made_outside_any_request_is_background()
    {
        var (pipeline, recorder, _) = Pipeline(new ManualClock(), (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        using var response = await Send(pipeline, "https://sap.invalid/b1s/v1/Invoices(5)", HttpMethod.Patch);

        var call = Assert.Single(recorder.SapCalls.Snapshot(_ => true).Items);
        Assert.Equal(("PATCH Invoices", ApiCallContext.Background), (call.Family, call.Origin));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, SapCallOutcome.Error)]
    [InlineData(HttpStatusCode.BadRequest, SapCallOutcome.Error)]
    [InlineData(HttpStatusCode.GatewayTimeout, SapCallOutcome.Timeout)]
    public async Task An_error_answer_is_kept_with_its_status(HttpStatusCode status, SapCallOutcome outcome)
    {
        var (pipeline, recorder, _) = Pipeline(new ManualClock(), (_, _) => Task.FromResult(new HttpResponseMessage(status)));

        using var response = await Send(pipeline, Stk);

        var call = Assert.Single(recorder.SapCalls.Snapshot(_ => true).Items);
        Assert.Equal((outcome, (short)status), (call.Outcome, call.StatusCode));
    }

    [Fact]
    public async Task A_read_that_ran_out_the_clients_timeout_is_a_timeout_and_presumed_stuck()
    {
        var clock = new ManualClock();
        var (pipeline, recorder, _) = Pipeline(clock, (_, _) =>
        {
            clock.Advance(ClientTimeout);
            throw new TaskCanceledException();
        });

        await Assert.ThrowsAsync<TaskCanceledException>(() => Send(pipeline, Stk));

        var call = Assert.Single(recorder.SapCalls.Snapshot(_ => true).Items);
        Assert.Equal((SapCallOutcome.Timeout, (short)0, true), (call.Outcome, call.StatusCode, call.PresumedStuck));
        Assert.Equal(100_000, call.AtSapMs);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(20, true)]
    public async Task A_caller_that_stopped_waiting_gave_up_and_a_read_sap_had_15_s_is_presumed_stuck(int seconds, bool stuck)
    {
        var clock = new ManualClock();
        var (pipeline, recorder, _) = Pipeline(clock, (_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            throw new OperationCanceledException();
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() => Send(pipeline, Stk));

        var call = Assert.Single(recorder.SapCalls.Snapshot(_ => true).Items);
        Assert.Equal((SapCallOutcome.GaveUp, stuck), (call.Outcome, call.PresumedStuck));
    }

    [Fact]
    public async Task A_read_its_full_family_refused_is_not_sent_and_never_reached_sap()
    {
        var sapAsked = false;
        var (pipeline, recorder, _) = Pipeline(new ManualClock(), (_, _) =>
        {
            sapAsked = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }, 1, true, out var gate, onlyStk: true);
        Assert.True(Assert.Single(gate.Families).TryEnter());

        await Assert.ThrowsAsync<SapReadFamilyFullException>(() => Send(pipeline, Stk));

        Assert.False(sapAsked);
        var call = Assert.Single(recorder.SapCalls.Snapshot(_ => true).Items);
        Assert.Equal((SapCallOutcome.NotSent, 0, (short)0), (call.Outcome, call.AtSapMs, call.StatusCode));
        Assert.False(CallTimingAggregator.ReachedSap(call));
    }

    [Fact]
    public async Task A_call_at_sap_is_listed_in_flight_with_its_stage_until_it_ends()
    {
        var atSap = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var clock = new ManualClock();
        var (pipeline, recorder, _) = Pipeline(clock, async (_, _) =>
        {
            atSap.SetResult();
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var sending = Send(pipeline, Stk);
        await atSap.Task;
        clock.Advance(TimeSpan.FromSeconds(42));

        var inFlight = Assert.Single(recorder.InFlight);
        var row = CallTimingAggregator.InFlightRow(inFlight, clock, familyLimited: true);
        Assert.Equal(("STK_ITEMS", "at-sap", 42_000, 42_000), (row.Family, row.Stage, row.AgeMs, row.AtSapMs));
        Assert.Equal(TimeSpan.Zero, inFlight.FamilyWait);

        release.SetResult();
        using var response = await sending;
        Assert.Empty(recorder.InFlight);
        Assert.Equal(42_000, Assert.Single(recorder.SapCalls.Snapshot(_ => true).Items).DurationMs);
    }

    [Fact]
    public void A_call_still_queued_is_shown_at_the_queue_it_is_in()
    {
        var clock = new ManualClock();
        var queuedForFamily = new SapCallInFlight(clock.GetTimestamp(), clock.GetUtcNow().UtcDateTime, "STK_ITEMS", "STK_ITEMS_A", ApiCallContext.Background, false);
        var queuedForSlot = new SapCallInFlight(clock.GetTimestamp(), clock.GetUtcNow().UtcDateTime, "STK_ITEMS", "STK_ITEMS_B", ApiCallContext.Background, false);
        queuedForSlot.FamilyWaited(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(4));

        Assert.Equal("family-slot", CallTimingAggregator.InFlightRow(queuedForFamily, clock, familyLimited: true).Stage);
        Assert.Equal("slot", CallTimingAggregator.InFlightRow(queuedForSlot, clock, familyLimited: true).Stage);
        Assert.Equal("slot", CallTimingAggregator.InFlightRow(queuedForFamily, clock, familyLimited: false).Stage);
        Assert.Equal(0, CallTimingAggregator.InFlightRow(queuedForSlot, clock, familyLimited: true).AtSapMs);
    }

    [Fact]
    public async Task Nothing_is_kept_when_timings_are_off()
    {
        var (pipeline, recorder, _) = Pipeline(new ManualClock(), (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)), enabled: false);

        using var response = await Send(pipeline, Stk);

        Assert.Empty(recorder.SapCalls.Snapshot(_ => true).Items);
        Assert.Equal(0, recorder.SapCalls.Added);
    }

    // ---- Inbound requests -------------------------------------------------------------------------

    [Fact]
    public async Task An_inbound_request_is_kept_by_its_route_template_never_its_path()
    {
        var clock = new ManualClock();
        var recorder = Recorder(clock);
        var middleware = new InboundCallTimingMiddleware(
            context =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(1_250));
                ApiCallContext.Current!.AddSapCall(900);
                ApiCallContext.Current!.AddSapCall(100);
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            },
            recorder);

        var context = Context("GET", "/api/BusinessPartner/CUST0001/balance", "api/BusinessPartner/{cardCode}/balance");
        context.Request.Headers[MobileHeaders.Platform] = "android";
        context.Request.Headers[MobileHeaders.AppId] = "com.kefalos.vansales";

        await middleware.InvokeAsync(context);

        var call = Assert.Single(recorder.InboundCalls.Snapshot(_ => true).Items);
        Assert.Equal(("GET", "api/BusinessPartner/{cardCode}/balance", (short)404), (call.Method, call.Route, call.StatusCode));
        Assert.Equal((1_250, 2, 1_000), (call.DurationMs, call.SapCalls, call.SapMs));
        Assert.Equal("Handset · kefalos-vansales", call.Caller);
        Assert.DoesNotContain("CUST0001", call.Route);
    }

    [Fact]
    public async Task A_request_that_threw_is_kept_as_a_500_and_the_exception_still_reaches_the_handler()
    {
        var recorder = Recorder(new ManualClock());
        var middleware = new InboundCallTimingMiddleware(_ => throw new InvalidOperationException("boom"), recorder);

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(Context("POST", "/api/Invoice", "api/Invoice")));

        Assert.Equal((short)500, Assert.Single(recorder.InboundCalls.Snapshot(_ => true).Items).StatusCode);
    }

    [Fact]
    public async Task A_request_no_endpoint_matched_is_kept_without_its_path()
    {
        var recorder = Recorder(new ManualClock());
        var middleware = new InboundCallTimingMiddleware(_ => Task.CompletedTask, recorder);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/wp-admin/CUST0001";

        await middleware.InvokeAsync(context);

        Assert.Equal(InboundCallTimingMiddleware.NoRoute, Assert.Single(recorder.InboundCalls.Snapshot(_ => true).Items).Route);
    }

    [Fact]
    public async Task A_hub_connection_is_not_kept()
    {
        var recorder = Recorder(new ManualClock());
        var middleware = new InboundCallTimingMiddleware(_ => Task.CompletedTask, recorder);
        var context = Context("GET", "/hubs/notifications", "hubs/notifications", new Microsoft.AspNetCore.SignalR.HubMetadata(typeof(object)));

        await middleware.InvokeAsync(context);

        Assert.Empty(recorder.InboundCalls.Snapshot(_ => true).Items);
    }

    // ---- Callers ----------------------------------------------------------------------------------

    [Fact]
    public void Callers_are_told_apart_by_their_headers_and_the_identities_that_authenticated()
    {
        Assert.Equal("Handset · kefalos-vansales", Caller(headers: [(MobileHeaders.Platform, "android"), (MobileHeaders.AppId, "kefalosvansales")]));
        Assert.Equal("Handset · unnamed app", Caller(headers: [(MobileHeaders.Version, "2.1.3")]));
        Assert.Equal(InboundCallerClassifier.Web, Caller(headers: [("X-Client-App", "web-portal")], key: "MainIntegration", user: "Cashier"));
        Assert.Equal(InboundCallerClassifier.WebBackground, Caller(headers: [("X-Client-App", "web-portal")], key: "MainIntegration"));
        Assert.Equal(InboundCallerClassifier.CustomerPortal, Caller(headers: [("X-Client-App", "web-portal"), ("X-Web-Surface", "customer-portal")], key: "MainIntegration"));
        Assert.Equal(InboundCallerClassifier.WebWithoutUser, Caller(headers: [("X-Client-App", "web-portal"), ("X-Web-Surface", "staff")], key: "MainIntegration"));
        Assert.Equal(InboundCallerClassifier.Web, Caller(headers: [("X-Client-App", "web-portal"), ("X-Web-Surface", "staff")], key: "MainIntegration", user: "Cashier"));
        Assert.Equal(InboundCallerClassifier.TillOrBearer, Caller(user: "TillOperator"));
        Assert.Equal("Key · TransferEventListener", Caller(key: "TransferEventListener"));
        Assert.Equal(InboundCallerClassifier.Anonymous, Caller());
    }

    [Fact]
    public void A_surface_header_from_outside_the_web_counts_for_nothing()
    {
        // Only the Web's own client header makes the surface count: a till or an integration that sent
        // it is still itself.
        Assert.Equal(InboundCallerClassifier.TillOrBearer, Caller(headers: [("X-Web-Surface", "customer-portal")], user: "TillOperator"));
        Assert.Equal("Key · TransferEventListener", Caller(headers: [("X-Web-Surface", "customer-portal")], key: "TransferEventListener"));
    }

    [Fact]
    public void The_api_reads_the_surface_header_the_web_sends()
    {
        Assert.Equal(ShopInventory.Web.Services.WebApiClientIdentity.SurfaceHeaderName, InboundCallerClassifier.SurfaceHeaderName);
        Assert.Equal(ShopInventory.Web.Services.WebApiClientIdentity.CustomerPortalSurface, InboundCallerClassifier.CustomerPortalSurface);
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private const string Stk = "https://sap.invalid/b1s/v1/SQLQueries('STK_ITEMS_AAAAAAAAAAAA')/List?$skip=0";

    private static class MobileHeaders
    {
        public const string Platform = "X-App-Platform";
        public const string AppId = "X-App-Id";
        public const string Version = "X-App-Version";
    }

    private static SapReadFamilyGate Gate(int limit = 2, ManualClock? clock = null) => new(
        Options.Create(Settings(limit)),
        (TimeProvider?)clock ?? TimeProvider.System);

    private static SAPSettings Settings(int limit) => new()
    {
        ReadFamilies = new Dictionary<string, SapReadFamilySettings>
        {
            ["STK_ITEMS"] = new() { MaxConcurrentReads = limit, SendInteractiveWhenFull = false },
            ["WHS_BATCHES"] = new() { MaxConcurrentReads = limit, SendInteractiveWhenFull = false },
        },
        ReadFamilyBackgroundMaxWaitSeconds = 0,
        ReadFamilyInteractiveMaxWaitSeconds = 0,
    };

    private static CallTimingRecorder Recorder(ManualClock clock, bool enabled = true) =>
        new(Options.Create(new CallTimingSettings { Enabled = enabled, SapCallCapacity = 100, InboundCallCapacity = 100 }), clock);

    private static (HttpMessageInvoker Pipeline, CallTimingRecorder Recorder, ManualClock Clock) Pipeline(
        ManualClock clock,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sap,
        int familyLimit = 2,
        bool enabled = true) => Pipeline(clock, sap, familyLimit, enabled, out _);

    private static (HttpMessageInvoker Pipeline, CallTimingRecorder Recorder, ManualClock Clock) Pipeline(
        ManualClock clock,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sap,
        int familyLimit,
        bool enabled,
        out SapReadFamilyGate gate,
        bool onlyStk = false)
    {
        var sapSettings = Settings(familyLimit);
        if (onlyStk)
        {
            sapSettings.ReadFamilies.Remove("WHS_BATCHES");
        }

        var settings = Options.Create(sapSettings);
        gate = new SapReadFamilyGate(settings, clock);
        var stuckReads = new SapStuckReadState(settings, clock);
        var recorder = Recorder(clock, enabled);

        var timing = new SAPCallTimingHandler(recorder, gate, stuckReads, ClientTimeout)
        {
            InnerHandler = new SAPReadFamilyHandler(gate, stuckReads, clock, NullLogger<SAPReadFamilyHandler>.Instance)
            {
                InnerHandler = new SAPConcurrencyHandler(Options.Create(new SAPSettings()), NullLogger<SAPConcurrencyHandler>.Instance, clock)
                {
                    InnerHandler = new ScriptedSap(sap),
                },
            },
        };

        return (new HttpMessageInvoker(timing), recorder, clock);
    }

    private static Task<HttpResponseMessage> Send(HttpMessageInvoker pipeline, string url, HttpMethod? method = null) =>
        pipeline.SendAsync(new HttpRequestMessage(method ?? HttpMethod.Get, url), CancellationToken.None);

    private static DefaultHttpContext Context(string method, string path, string template, params object[] metadata)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(template),
            0,
            new EndpointMetadataCollection(metadata),
            template));
        return context;
    }

    private static string Caller((string Name, string Value)[]? headers = null, string? key = null, string? user = null)
    {
        var context = new DefaultHttpContext();
        foreach (var (name, value) in headers ?? [])
        {
            context.Request.Headers[name] = value;
        }

        var identities = new List<ClaimsIdentity>();
        if (key is not null)
        {
            identities.Add(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, key),
                    new Claim(ClaimTypes.AuthenticationMethod, ApiSchemes.ApiKey),
                    new Claim("api_key_name", key),
                ],
                ApiSchemes.ApiKey));
        }

        if (user is not null)
        {
            identities.Add(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "someone"), new Claim(ClaimTypes.Role, user)],
                ApiSchemes.Jwt));
        }

        context.User = new ClaimsPrincipal(identities);
        return InboundCallerClassifier.Classify(context);
    }

    private sealed class ScriptedSap(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            answer(request, cancellationToken);
    }

    /// <summary>A clock that moves only when told to, on both the wall clock and the timestamp clock.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private static readonly DateTimeOffset Start = new(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);
        private long _ticks;

        public override DateTimeOffset GetUtcNow() => Start + TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks) + 1;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
