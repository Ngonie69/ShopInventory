using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ShopInventory.Features.Maintenance;
using ShopInventory.Middleware;

namespace ShopInventory.Tests;

/// <summary>
/// The lockout as a handset meets it: a 503 it can read, a <c>Retry-After</c> it can honour, and —
/// for everything else — a request that simply carries on.
///
/// The gate's own tests cover which requests are refused. These cover the plumbing around that
/// decision, which is where the other half of the failures live: recognising a phone from its
/// headers, not touching anything else, and writing a body the apps' hand-written error readers
/// can actually parse.
/// </summary>
public sealed class MobileMaintenanceMiddlewareTests
{
    private static readonly DateTime Now = new(2026, 9, 22, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task With_the_switch_off_nothing_is_touched()
    {
        var result = await InvokeAsync(MobileMaintenanceState.Off, headers: Phone());

        Assert.True(result.ReachedTheApi);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
    }

    [Fact]
    public async Task A_phone_posting_during_a_lockout_gets_a_503_it_can_read()
    {
        var result = await InvokeAsync(Lockout(), headers: Phone(), method: "POST", path: "/api/vansales/sales");

        Assert.False(result.ReachedTheApi);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);

        var body = result.Body;
        Assert.Equal("Maintenance.MobileTransactionsSuspended", body.GetProperty("code").GetString());
        Assert.Equal("Down for the stock migration.", body.GetProperty("message").GetString());
        Assert.True(body.GetProperty("maintenance").GetBoolean());
        Assert.Equal("Transactions", body.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task The_phone_is_told_how_long_to_wait()
    {
        // So a van full of handsets backs off instead of retrying in a loop against an API that is
        // mid-maintenance.
        var result = await InvokeAsync(
            Lockout(endsAtUtc: Now.AddMinutes(30)),
            headers: Phone(),
            method: "POST",
            path: "/api/vansales/sales");

        Assert.Equal("1800", result.RetryAfter);
        Assert.Equal(1800, result.Body.GetProperty("retryAfterSeconds").GetInt32());
    }

    [Fact]
    public async Task The_web_is_not_a_phone()
    {
        // The Web calls the API with none of the app headers. If this ever stops holding, throwing
        // the switch takes the office down with the vans.
        var result = await InvokeAsync(
            Lockout(MobileMaintenanceScope.All),
            headers: new Dictionary<string, string>(),
            method: "POST",
            path: "/api/invoice");

        Assert.True(result.ReachedTheApi);
    }

    [Fact]
    public async Task A_caller_naming_a_platform_that_is_not_android_is_not_a_phone()
    {
        var result = await InvokeAsync(
            Lockout(MobileMaintenanceScope.All),
            headers: new Dictionary<string, string> { ["X-App-Platform"] = "windows", ["X-App-Version"] = "3.1.0" },
            method: "POST",
            path: "/api/desktopintegration/sales");

        Assert.True(result.ReachedTheApi);
    }

    [Fact]
    public async Task An_older_build_that_sends_only_a_device_model_is_still_a_phone()
    {
        // The POD app has named its handset on X-Device-Model since before the version headers
        // existed. A lockout it could walk through would be no lockout at all.
        var result = await InvokeAsync(
            Lockout(),
            headers: new Dictionary<string, string> { ["X-Device-Model"] = "TECNO KG5j" },
            method: "POST",
            path: "/api/crates/pods");

        Assert.False(result.ReachedTheApi);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
    }

    [Fact]
    public async Task A_lockout_aimed_elsewhere_lets_this_app_through()
    {
        var result = await InvokeAsync(
            Lockout(apps: ["cheeseman-driver"]),
            headers: Phone(appId: "com.kefalos.vansales"),
            method: "POST",
            path: "/api/vansales/sales");

        Assert.True(result.ReachedTheApi);
    }

    [Fact]
    public async Task A_window_that_has_run_out_lets_the_phone_trade_again()
    {
        // Enabled, but past its end. Nobody has to be awake to lift it.
        var result = await InvokeAsync(
            Lockout(endsAtUtc: Now.AddMinutes(-1)),
            headers: Phone(),
            method: "POST",
            path: "/api/vansales/sales");

        Assert.True(result.ReachedTheApi);
    }

    [Fact]
    public async Task A_header_full_of_newlines_cannot_write_its_own_lines_into_the_log()
    {
        // Every value logged on a refusal comes off the request, and a refused request is by
        // definition one somebody may be probing with. Without sanitising, a caller could forge
        // whole log entries in the file this feature is read through during an incident.
        const string forged = "[00:00:00 INF] Refused nothing: lockout is off";

        var headers = Phone();
        headers["X-Device-Model"] = "Pixel\r\n" + forged;
        headers["X-App-Version"] = "2.0.1\nfabricated";

        var result = await InvokeAsync(Lockout(), headers, method: "POST", path: "/api/vansales/sales");

        Assert.False(result.ReachedTheApi);
        var logged = Assert.Single(result.Logged);

        // The point is not that the attacker's text disappears — it is theirs, and a reader should
        // see what they sent. It is that it cannot start a line. One refused request writes exactly
        // one line, so nothing a caller sends can be read later as a separate entry.
        Assert.DoesNotContain('\n', logged);
        Assert.DoesNotContain('\r', logged);
        Assert.Single(logged.Split('\n'));

        // Still readable: sanitising replaces the control characters rather than dropping the value.
        Assert.Contains("Pixel", logged, StringComparison.Ordinal);
        Assert.Contains(forged, logged, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> Phone(string appId = "com.kefalos.vansales") => new()
    {
        ["X-App-Id"] = appId,
        ["X-App-Platform"] = "android",
        ["X-App-Version"] = "2.0.1"
    };

    private static MobileMaintenanceState Lockout(
        MobileMaintenanceScope scope = MobileMaintenanceScope.Transactions,
        IReadOnlyList<string>? apps = null,
        DateTime? endsAtUtc = null) =>
        new(
            Enabled: true,
            Scope: scope,
            Message: "Down for the stock migration.",
            AppIds: apps ?? [],
            StartedAtUtc: Now.AddMinutes(-5),
            EndsAtUtc: endsAtUtc,
            UpdatedBy: "ngoni");

    private sealed record Result(
        bool ReachedTheApi, int StatusCode, string? RetryAfter, JsonElement Body, IReadOnlyList<string> Logged);

    private static async Task<Result> InvokeAsync(
        MobileMaintenanceState state,
        IDictionary<string, string> headers,
        string method = "GET",
        string path = "/api/product")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var reachedTheApi = false;
        var logger = new CapturingLogger();
        var middleware = new MobileMaintenanceMiddleware(
            _ =>
            {
                reachedTheApi = true;
                return Task.CompletedTask;
            },
            logger,
            new StubStore(state),
            new FixedClock(Now));

        await middleware.InvokeAsync(context);

        responseBody.Position = 0;
        var body = responseBody.Length == 0
            ? default
            : JsonDocument.Parse(responseBody).RootElement.Clone();

        return new Result(
            reachedTheApi,
            context.Response.StatusCode,
            context.Response.Headers.RetryAfter.FirstOrDefault(),
            body,
            logger.Messages);
    }

    /// <summary>Keeps the rendered log messages so a test can assert on what was written.</summary>
    private sealed class CapturingLogger : ILogger<MobileMaintenanceMiddleware>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }

    private sealed class StubStore(MobileMaintenanceState state) : IMobileMaintenanceStore
    {
        public MobileMaintenanceState Current { get; } = state;

        public Task UpdateAsync(MobileMaintenanceState newState, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedClock(DateTime nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(nowUtc, TimeSpan.Zero);
    }
}
