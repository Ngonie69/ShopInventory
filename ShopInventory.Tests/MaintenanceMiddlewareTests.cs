using System.Security.Claims;
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
public sealed class MaintenanceMiddlewareTests
{
    private static readonly DateTime Now = new(2026, 9, 22, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task With_the_switch_off_nothing_is_touched()
    {
        var result = await InvokeAsync(MaintenanceState.Off, headers: Phone());

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
            Lockout(MaintenanceScope.All),
            headers: new Dictionary<string, string>(),
            method: "POST",
            path: "/api/invoice");

        Assert.True(result.ReachedTheApi);
    }

    [Fact]
    public async Task A_caller_naming_a_platform_that_is_not_android_is_not_a_phone()
    {
        var result = await InvokeAsync(
            Lockout(MaintenanceScope.All),
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

    [Fact]
    public async Task The_method_and_an_unrecognised_app_id_cannot_write_their_own_lines_either()
    {
        // The test above leaves two of the five values on that line untested: it sends a clean
        // "POST", and an app id the catalogue recognises, so the line logs the resolved PolicyKey
        // — a catalogue constant — rather than the raw header behind it. Both are caller-supplied on
        // a real request. An app id nothing in the catalogue matches falls through to AppId as sent,
        // which is the case that has to hold.
        const string forged = "[00:00:00 INF] Refused nothing: lockout is off";

        var headers = Phone(appId: "com.rogue.app\r\n" + forged);

        // Not a read, so the lockout still refuses it: HttpMethods.IsPost is false for anything but
        // "POST" exactly, and a method the gate cannot classify is one it does not wave through.
        var result = await InvokeAsync(
            Lockout(), headers, method: "POST\r\n" + forged, path: "/api/vansales/sales");

        Assert.False(result.ReachedTheApi);
        var logged = Assert.Single(result.Logged);

        Assert.DoesNotContain('\n', logged);
        Assert.DoesNotContain('\r', logged);
        Assert.Single(logged.Split('\n'));

        // Both values survive in readable form; neither can start a line.
        Assert.Contains("POST", logged, StringComparison.Ordinal);
        Assert.Contains("com.rogue.app", logged, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> Phone(string appId = "com.kefalos.vansales") => new()
    {
        ["X-App-Id"] = appId,
        ["X-App-Platform"] = "android",
        ["X-App-Version"] = "2.0.1"
    };

    private static MaintenanceState Lockout(
        MaintenanceScope scope = MaintenanceScope.Transactions,
        IReadOnlyList<string>? apps = null,
        IReadOnlyList<MaintenanceAudience>? audiences = null,
        DateTime? endsAtUtc = null) =>
        new(
            Enabled: true,
            Scope: scope,
            Audiences: audiences ?? [MaintenanceAudience.MobileApps],
            Message: "Down for the stock migration.",
            AppIds: apps ?? [],
            StartedAtUtc: Now.AddMinutes(-5),
            EndsAtUtc: endsAtUtc,
            UpdatedBy: "ngoni");

    private sealed record Result(
        bool ReachedTheApi, int StatusCode, string? RetryAfter, JsonElement Body, IReadOnlyList<string> Logged);

    private static async Task<Result> InvokeAsync(
        MaintenanceState state,
        IDictionary<string, string> headers,
        string method = "GET",
        string path = "/api/product",
        MaintenanceStage stage = MaintenanceStage.BeforeAuthentication,
        ClaimsPrincipal? user = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        if (user is not null)
        {
            context.User = user;
        }

        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var reachedTheApi = false;
        var logger = new CapturingLogger();
        var middleware = new MaintenanceMiddleware(
            _ =>
            {
                reachedTheApi = true;
                return Task.CompletedTask;
            },
            logger,
            new StubStore(state),
            new FixedClock(Now),
            stage);

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

    [Fact]
    public async Task A_newline_in_the_path_cannot_write_its_own_line_into_the_log()
    {
        // Separate from the header case above, because the path needs a different guard and the
        // obvious one does nothing. HttpRequest.Path is a PathString whose implicit string
        // conversion is ToUriComponent(), so sanitising the struct hands the sanitiser a value
        // where the newline is already %0A — the wrapper looks load-bearing and is not. This test
        // fails if the .Value goes back to being the struct.
        const string forged = "[00:00:00 INF] lockout is off";

        var result = await InvokeAsync(
            Lockout(),
            Phone(),
            method: "POST",
            path: "/api/vansales/sales\n" + forged);

        Assert.False(result.ReachedTheApi);
        var logged = Assert.Single(result.Logged);

        Assert.DoesNotContain('\n', logged);
        Assert.DoesNotContain('\r', logged);

        // The raw text is still there to read — it is what the caller sent, and the point is only
        // that it cannot start a line. Its presence unencoded is also what proves .Value was used:
        // through the PathString it would have arrived percent-encoded.
        Assert.Contains(forged, logged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_portal_is_not_judged_before_authentication()
    {
        // The stage split, stated as a test. Judged early, the portal would be refused before
        // anyone had established whether an Admin was behind the request, and the exemption would
        // never fire — a lockout that stopped the people running the maintenance.
        var result = await InvokeAsync(
            Lockout(audiences: [MaintenanceAudience.WebPortal]),
            WebPortalHeaders,
            method: "POST",
            path: "/api/invoice",
            stage: MaintenanceStage.BeforeAuthentication);

        Assert.True(result.ReachedTheApi);
    }

    [Fact]
    public async Task The_portal_is_refused_after_authorization()
    {
        var result = await InvokeAsync(
            Lockout(audiences: [MaintenanceAudience.WebPortal]),
            WebPortalHeaders,
            method: "POST",
            path: "/api/invoice",
            stage: MaintenanceStage.AfterAuthorization);

        Assert.False(result.ReachedTheApi);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("WebPortal", result.Body.GetProperty("audience").GetString());
    }

    [Fact]
    public async Task A_phone_is_not_judged_again_after_authorization()
    {
        // The other half of the split. Judged twice, a refusal would be logged twice and read as
        // two attempts — and the second stage runs after authentication, which is the database
        // work the early stage exists to avoid.
        var result = await InvokeAsync(
            Lockout(),
            Phone(),
            method: "POST",
            path: "/api/vansales/sales",
            stage: MaintenanceStage.AfterAuthorization);

        Assert.True(result.ReachedTheApi);
    }

    [Fact]
    public async Task An_admin_passes_through_a_frozen_portal()
    {
        var result = await InvokeAsync(
            Lockout(MaintenanceScope.All, audiences: [MaintenanceAudience.WebPortal]),
            WebPortalHeaders,
            method: "POST",
            path: "/api/invoice",
            stage: MaintenanceStage.AfterAuthorization,
            user: SignedInAs("Admin"));

        Assert.True(result.ReachedTheApi);
    }

    [Fact]
    public async Task The_api_keys_own_admin_role_does_not_exempt_the_user_behind_it()
    {
        // The trap this feature had to step around. The Web sends its X-API-Key and the signed-in
        // user's token together, and the key's identity carries Admin — so asking the merged
        // principal would exempt a cashier and the freeze would stop nobody at all.
        var result = await InvokeAsync(
            Lockout(MaintenanceScope.All, audiences: [MaintenanceAudience.WebPortal]),
            WebPortalHeaders,
            method: "POST",
            path: "/api/invoice",
            stage: MaintenanceStage.AfterAuthorization,
            user: WebRequestWithApiKeyAndUser("Cashier"));

        Assert.False(result.ReachedTheApi);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
    }

    private static readonly Dictionary<string, string> WebPortalHeaders = new()
    {
        ["X-Client-App"] = "web-portal"
    };

    private static ClaimsPrincipal SignedInAs(string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "ApiBearer"));

    /// <summary>
    /// What the API actually sees on a call from the Web: the key's identity, carrying Admin, and
    /// the signed-in user's beside it.
    /// </summary>
    private static ClaimsPrincipal WebRequestWithApiKeyAndUser(string userRole)
    {
        var key = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Role, "Admin"),
                new Claim(ClaimTypes.AuthenticationMethod, "ApiKey"),
                new Claim("api_key_name", "MainIntegration")
            ],
            "ApiKey");

        var user = new ClaimsIdentity([new Claim(ClaimTypes.Role, userRole)], "ApiBearer");

        return new ClaimsPrincipal([key, user]);
    }

    /// <summary>Keeps the rendered log messages so a test can assert on what was written.</summary>
    private sealed class CapturingLogger : ILogger<MaintenanceMiddleware>
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

    private sealed class StubStore(MaintenanceState state) : IMaintenanceStore
    {
        public MaintenanceState Current { get; } = state;

        public Task UpdateAsync(MaintenanceState newState, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedClock(DateTime nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(nowUtc, TimeSpan.Zero);
    }
}
