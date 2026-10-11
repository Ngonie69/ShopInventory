using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShopInventory.Services.SlHealth;

namespace ShopInventory.Tests;

/// <summary>
/// The SL Health reader as <c>Program.cs</c> registers it, over a stubbed transport and a clock the
/// test moves.
/// </summary>
/// <remarks>
/// Built through <see cref="SlHealthServiceCollectionExtensions.AddSlHealth"/> rather than by hand, so
/// the three-second timeout and the logging under test are the ones production gets. Every log line
/// from every category is kept, at Trace, so a test can prove the token is in none of them.
/// </remarks>
internal sealed class SlHealthHarness : IDisposable
{
    public const string Token = "test-only-shopinventory-token-0000001";
    public const string Url = "http://slhealth.test:5080";

    /// <summary>
    /// Every way of not having an answer fit to act on. Each must leave the API doing exactly what it
    /// did before it could ask SL Health.
    /// </summary>
    public static readonly string[] NoAnswerCases =
        ["off", "unreachable", "slow", "stale", "unknown advice", "refused", "too old"];

    private readonly ServiceProvider _services;

    public SlHealthHarness(string? url = Url, string? token = Token)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SlHealth:Url"] = url,
                ["SlHealth:Token"] = token
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(new SinkProvider(Logs)));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSlHealth(configuration).ConfigurePrimaryHttpMessageHandler(() => Transport);

        _services = services.BuildServiceProvider();
        Monitor = _services.GetRequiredService<SlHealthMonitor>();
    }

    public ManualClock Clock { get; } = new();

    public StubTransport Transport { get; } = new();

    public CapturingLogger Logs { get; } = new();

    public SlHealthMonitor Monitor { get; }

    public IServiceProvider Services => _services;

    /// <summary>SL Health answers with this from now on.</summary>
    public void Answers(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        Transport.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

    /// <summary>SL Health answers with this, and the reader has read it.</summary>
    public async Task HasHeardAsync(string json)
    {
        Answers(json);
        await Monitor.RefreshAsync();
    }

    /// <summary>Puts the reader in one of <see cref="NoAnswerCases"/>, asked the way the refresher asks.</summary>
    public static async Task<SlHealthHarness> WithNoAnswerAsync(string noAnswerCase)
    {
        // What SL Health would be saying if it could be believed: down, restarting, logins failing. A
        // reader that leaked any of it into behaviour would show up in every test that uses this.
        var alarming = SlHealthJson.Status(
            level: "Down", advice: "Unavailable", loginOk: false, queryOk: false, serverToolsOk: false,
            restartTarget: "ServiceLayer", maintenanceReason: "HANA backup");

        var harness = noAnswerCase == "off" ? new SlHealthHarness(url: "", token: "") : new SlHealthHarness();

        switch (noAnswerCase)
        {
            case "off":
                harness.Answers(alarming);
                break;
            case "unreachable":
                harness.Transport.Respond = (_, _) => throw new HttpRequestException("No connection could be made.");
                break;
            case "slow":
                harness.Transport.Respond = async (_, token) =>
                {
                    await Task.Delay(Timeout.Infinite, token);
                    throw new InvalidOperationException("unreachable");
                };
                break;
            case "stale":
                harness.Answers(SlHealthJson.Status(
                    level: "Down", advice: "Unknown", stale: true, loginOk: false, queryOk: false, serverToolsOk: false,
                    restartTarget: "ServiceLayer", maintenanceReason: "HANA backup"));
                break;
            case "unknown advice":
                harness.Answers(SlHealthJson.Status(
                    level: "Unknown", advice: "Unknown", loginOk: false, queryOk: false, serverToolsOk: false,
                    restartTarget: "ServiceLayer", maintenanceReason: "HANA backup"));
                break;
            case "refused":
                harness.Answers("", HttpStatusCode.Unauthorized);
                break;
            case "too old":
                harness.Answers(alarming);
                await harness.Monitor.RefreshAsync();
                harness.Transport.Respond = (_, _) => throw new HttpRequestException("No connection could be made.");
                harness.Clock.Advance(SlHealthMonitor.MaxAge + TimeSpan.FromSeconds(1));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(noAnswerCase), noAnswerCase, "Not one of NoAnswerCases.");
        }

        await harness.Monitor.RefreshAsync();
        return harness;
    }

    public void Dispose() => _services.Dispose();

    internal sealed class StubTransport : HttpMessageHandler
    {
        public List<(Uri? Uri, string? Authorization)> Requests { get; } = [];

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => throw new InvalidOperationException("SL Health was not expected to be asked.");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri, request.Headers.Authorization?.ToString()));
            return Respond(request, cancellationToken);
        }
    }

    private sealed class SinkProvider(CapturingLogger sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => sink;

        public void Dispose()
        {
        }
    }
}

/// <summary>SL Health's status answer, written the way SL Health writes it.</summary>
internal static class SlHealthJson
{
    public static string Status(
        string level = "Healthy",
        string advice = "Proceed",
        bool stale = false,
        bool pingOk = true,
        bool loginOk = true,
        bool queryOk = true,
        bool serverToolsOk = true,
        string? maintenanceReason = null,
        DateTimeOffset? maintenanceUntil = null,
        string? restartTarget = null,
        string[]? reasons = null,
        int version = 1,
        DateTimeOffset? now = null)
    {
        var at = (now ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)).ToUnixTimeMilliseconds();

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = version,
            ["checkedAt"] = at,
            ["stale"] = stale,
            ["level"] = level,
            ["label"] = level switch { "Critical" => "EXTREME LOAD", "Down" => "DOWN", "Warning" => "HIGH LOAD", _ => level.ToUpperInvariant() },
            ["since"] = at - 45_000,
            ["reasons"] = reasons ?? [],
            ["advice"] = advice,
            ["ping"] = new { name = "Ping", ok = pingOk, ms = 14, error = (string?)null },
            ["login"] = new { name = "Login", ok = loginOk, ms = 310, error = loginOk ? null : "HTTP 401" },
            ["queries"] = new[] { new { name = "Items", ok = queryOk, ms = 62, error = queryOk ? null : "timed out after 30 s" } },
            ["serverTools"] = new { name = "Server Tools", ok = serverToolsOk, ms = 3, error = serverToolsOk ? null : "HTTP 503" },
            ["maintenance"] = maintenanceReason is null
                ? null
                : new { reason = maintenanceReason, until = (maintenanceUntil ?? DateTimeOffset.FromUnixTimeMilliseconds(at).AddHours(3)).ToUnixTimeMilliseconds() },
            ["restart"] = restartTarget is null ? null : new { target = restartTarget, startedAt = at - 20_000 },
            // Sent by SL Health and deliberately not read: see SlHealthStatus.
            ["certificate"] = new { sha256 = "AA11", sha1 = "BB22", notAfter = at + 86_400_000L, firstSeen = at - 86_400_000L },
            // A field this build has never heard of, as a later SL Health may add without raising the version.
            ["somethingNewer"] = new { added = true }
        });
    }
}
