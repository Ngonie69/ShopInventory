using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ShopInventory.Services.SlHealth;

namespace ShopInventory.Tests;

/// <summary>
/// When the API has an answer from SL Health fit to act on, and above all when it has not: switched
/// off, unreachable, slow, stale, without advice, refused or too old must all read as "no SL Health".
/// </summary>
public sealed class SlHealthMonitorTests
{
    [Fact]
    public async Task A_fresh_answer_with_advice_is_kept()
    {
        using var slHealth = new SlHealthHarness();

        await slHealth.HasHeardAsync(SlHealthJson.Status(
            level: "Critical", advice: "HoldBackground", serverToolsOk: false,
            reasons: ["Server Tools (SLD, licensing) not answering: HTTP 503. New logins will fail"]));

        var status = slHealth.Monitor.Current;
        Assert.NotNull(status);
        Assert.Equal("Critical", status.Level);
        Assert.Equal(SlHealthAdvice.HoldBackground, status.AdviceValue);
        Assert.Equal("Server Tools (SLD, licensing) not answering: HTTP 503. New logins will fail", Assert.Single(status.Reasons!));
        Assert.False(status.ServerTools!.Ok);
        Assert.True(status.Queries![0].Ok);
    }

    [Fact]
    public async Task It_asks_the_status_address_with_the_token_as_a_bearer_header()
    {
        using var slHealth = new SlHealthHarness(url: "http://slhealth.test:5080/");

        await slHealth.HasHeardAsync(SlHealthJson.Status());

        var request = Assert.Single(slHealth.Transport.Requests);
        Assert.Equal("http://slhealth.test:5080/api/integration/status", request.Uri!.ToString());
        Assert.Equal($"Bearer {SlHealthHarness.Token}", request.Authorization);
    }

    [Theory]
    [InlineData("", SlHealthHarness.Token)]
    [InlineData(SlHealthHarness.Url, "")]
    [InlineData("   ", "   ")]
    [InlineData("slhealth.test:5080", SlHealthHarness.Token)]
    [InlineData("ftp://slhealth.test", SlHealthHarness.Token)]
    public async Task Without_an_address_and_a_token_it_never_asks(string url, string token)
    {
        using var slHealth = new SlHealthHarness(url, token);

        await slHealth.Monitor.RefreshAsync();

        Assert.False(slHealth.Monitor.IsConfigured);
        Assert.Empty(slHealth.Transport.Requests);
        Assert.Null(slHealth.Monitor.Current);
    }

    [Theory]
    [MemberData(nameof(NoAnswerCases))]
    public async Task Every_way_of_having_no_answer_reads_as_no_sl_health(string noAnswerCase)
    {
        using var slHealth = await SlHealthHarness.WithNoAnswerAsync(noAnswerCase);

        Assert.Null(slHealth.Monitor.Current);
        Assert.Null(slHealth.Monitor.TroubleSinceUtc);
    }

    [Fact]
    public async Task A_slow_sl_health_is_given_up_on_after_three_seconds()
    {
        using var slHealth = new SlHealthHarness();
        slHealth.Transport.Respond = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        };

        var clock = Stopwatch.StartNew();
        await slHealth.Monitor.RefreshAsync();
        clock.Stop();

        Assert.Equal(TimeSpan.FromSeconds(3), SlHealthClient.Timeout);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(8));
        Assert.Null(slHealth.Monitor.Current);

        var client = slHealth.Services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ISlHealthClient));
        Assert.Equal(SlHealthClient.Timeout, client.Timeout);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task An_error_answer_is_no_answer(HttpStatusCode status)
    {
        using var slHealth = new SlHealthHarness();
        slHealth.Answers(SlHealthJson.Status(level: "Down", advice: "Unavailable"), status);

        await slHealth.Monitor.RefreshAsync();

        Assert.Null(slHealth.Monitor.Current);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("<html><body>Sign in</body></html>")]
    [InlineData("""{"version":1,"advice":"Unavailable","level":""")]
    [InlineData("""{"version":1,"advice":42,"stale":"no"}""")]
    public async Task An_answer_that_cannot_be_read_is_no_answer(string body)
    {
        using var slHealth = new SlHealthHarness();

        await slHealth.HasHeardAsync(body);

        Assert.Null(slHealth.Monitor.Current);
    }

    [Theory]
    [InlineData("Sideways")]
    [InlineData("3")]
    [InlineData("")]
    public async Task Advice_this_build_does_not_know_is_no_advice(string advice)
    {
        using var slHealth = new SlHealthHarness();

        await slHealth.HasHeardAsync(SlHealthJson.Status(level: "Down", advice: advice));

        Assert.Null(slHealth.Monitor.Current);
    }

    [Fact]
    public async Task A_newer_shape_than_this_build_reads_is_no_answer()
    {
        using var slHealth = new SlHealthHarness();

        await slHealth.HasHeardAsync(SlHealthJson.Status(level: "Down", advice: "Unavailable", version: 2));

        Assert.Null(slHealth.Monitor.Current);
    }

    [Fact]
    public async Task An_answer_far_larger_than_a_real_one_is_no_answer()
    {
        using var slHealth = new SlHealthHarness();
        var padded = SlHealthJson.Status(reasons: [new string('x', SlHealthClient.MaxResponseBytes + 1)]);

        await slHealth.HasHeardAsync(padded);

        Assert.Null(slHealth.Monitor.Current);
    }

    [Fact]
    public async Task An_answer_is_used_for_a_minute_and_then_ignored()
    {
        using var slHealth = new SlHealthHarness();
        await slHealth.HasHeardAsync(SlHealthJson.Status(level: "Down", advice: "Unavailable"));

        slHealth.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.NotNull(slHealth.Monitor.Current);

        slHealth.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(slHealth.Monitor.Current);
    }

    [Fact]
    public async Task A_failed_ask_keeps_the_last_answer_until_it_is_too_old()
    {
        using var slHealth = new SlHealthHarness();
        await slHealth.HasHeardAsync(SlHealthJson.Status(level: "Down", advice: "Unavailable"));

        slHealth.Transport.Respond = (_, _) => throw new HttpRequestException("No connection could be made.");
        for (var asked = 0; asked < 4; asked++)
        {
            slHealth.Clock.Advance(SlHealthMonitor.RefreshInterval);
            await slHealth.Monitor.RefreshAsync();
            Assert.Equal(SlHealthAdvice.Unavailable, slHealth.Monitor.Current?.AdviceValue);
        }

        slHealth.Clock.Advance(SlHealthMonitor.RefreshInterval);
        await slHealth.Monitor.RefreshAsync();

        Assert.Null(slHealth.Monitor.Current);
    }

    [Fact]
    public async Task It_is_read_again_as_soon_as_it_answers_again()
    {
        using var slHealth = await SlHealthHarness.WithNoAnswerAsync("unreachable");

        await slHealth.HasHeardAsync(SlHealthJson.Status());

        Assert.Equal(SlHealthAdvice.Proceed, slHealth.Monitor.Current?.AdviceValue);
    }

    [Fact]
    public async Task The_token_is_in_no_log_line_whatever_happens()
    {
        using var slHealth = new SlHealthHarness();

        await slHealth.HasHeardAsync(SlHealthJson.Status());
        slHealth.Answers("", HttpStatusCode.Unauthorized);
        await slHealth.Monitor.RefreshAsync();
        slHealth.Answers("not json");
        await slHealth.Monitor.RefreshAsync();
        slHealth.Transport.Respond = (request, _) =>
            throw new HttpRequestException($"Refused {request.RequestUri} carrying {request.Headers.Authorization}");
        await slHealth.Monitor.RefreshAsync();
        await slHealth.HasHeardAsync(SlHealthJson.Status());

        // Trace is on for every category. The last failure above is the worst case: a transport error
        // that quotes the header, which the HTTP client factory's own logger would have written whole.
        Assert.Contains(slHealth.Logs.Entries, entry => entry.Message.Contains("SL Health is not being read"));
        Assert.DoesNotContain(slHealth.Logs.Entries, entry =>
            entry.Message.Contains(SlHealthHarness.Token) || (entry.Exception?.ToString().Contains(SlHealthHarness.Token) ?? false));
    }

    [Fact]
    public async Task It_says_once_that_it_has_stopped_reading_and_once_that_it_reads_again()
    {
        using var slHealth = new SlHealthHarness();
        slHealth.Transport.Respond = (_, _) => throw new HttpRequestException("No connection could be made.");

        for (var asked = 0; asked < 5; asked++)
        {
            await slHealth.Monitor.RefreshAsync();
        }

        await slHealth.HasHeardAsync(SlHealthJson.Status());
        await slHealth.Monitor.RefreshAsync();

        Assert.Single(slHealth.Logs.Entries, entry => entry.Message.Contains("SL Health is not being read"));
        Assert.Single(slHealth.Logs.Entries, entry => entry.Message.Contains("SL Health is being read"));
    }

    [Fact]
    public void The_reader_has_nowhere_to_put_the_certificate_sl_health_reports()
    {
        // Pinning a thumbprint because SL Health reported it would be trusting whatever answers on the
        // port. The surest way never to do it is never to read it.
        var names = typeof(SlHealthStatus).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(SlHealthStatus).Namespace)
            .SelectMany(type => type.GetProperties().Select(property => $"{type.Name}.{property.Name}"));

        Assert.DoesNotContain(names, name =>
            name.Contains("Certificate", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Sha", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Thumbprint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_refresher_does_not_hold_up_start_up_and_asks_nothing_when_switched_off()
    {
        using var slow = new SlHealthHarness();
        var asked = new TaskCompletionSource();
        slow.Transport.Respond = async (_, token) =>
        {
            asked.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        };

        using var refresher = new SlHealthRefresher(slow.Monitor);
        var clock = Stopwatch.StartNew();
        await refresher.StartAsync(CancellationToken.None);
        clock.Stop();
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await refresher.StopAsync(CancellationToken.None);

        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        using var off = new SlHealthHarness(url: "", token: "");
        using var idle = new SlHealthRefresher(off.Monitor);
        await idle.StartAsync(CancellationToken.None);
        await idle.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(off.Transport.Requests);
    }

    public static TheoryData<string> NoAnswerCases() => new(SlHealthHarness.NoAnswerCases);
}
