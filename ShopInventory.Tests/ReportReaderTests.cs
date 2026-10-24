using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The van reports read through one reader, so a refusal reads the same on every page. Each page used
/// to turn any failure into null and then guess the reason — "check that the API is reachable" —
/// including when the API had answered with its own.
/// </summary>
public class ReportReaderTests
{
    private sealed class Report
    {
        public int Rows { get; set; }
    }

    private sealed class Answer(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new Answer(respond)) { BaseAddress = new Uri("http://api.test/") };

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string type = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, type) };

    private static Task<ShopInventory.Web.Models.ReportResult<Report>> ReadAsync(HttpClient client, CancellationToken token = default) =>
        ReportReader.GetAsync<Report>(client, NullLogger.Instance, "api/report", "performance report", token);

    [Fact]
    public async Task A_report_is_handed_back()
    {
        var result = await ReadAsync(Client(_ => Json(HttpStatusCode.OK, "{\"rows\":3}")));

        Assert.Equal(3, result.Value!.Rows);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task The_apis_own_reason_reaches_the_screen()
    {
        var result = await ReadAsync(Client(_ => Json(
            HttpStatusCode.BadRequest,
            "{\"title\":\"Bad Request\",\"status\":400,\"detail\":\"Choose a period of 400 days or fewer.\"}",
            "application/problem+json")));

        Assert.Null(result.Value);
        Assert.Equal("Choose a period of 400 days or fewer.", result.Error);
    }

    [Fact]
    public async Task A_refusal_with_nothing_to_say_names_the_report_and_the_status()
    {
        var result = await ReadAsync(Client(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        Assert.Equal("The performance report could not be loaded: the server answered 500.", result.Error);
    }

    [Fact]
    public async Task An_api_that_cannot_be_reached_is_said_to_be_unreachable()
    {
        var result = await ReadAsync(Client(_ => throw new HttpRequestException("No connection could be made")));

        Assert.Equal("The performance report could not be loaded: the API could not be reached.", result.Error);
    }

    [Fact]
    public async Task A_load_the_page_cancelled_is_not_reported_as_a_failure()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReadAsync(Client(_ => Json(HttpStatusCode.OK, "{\"rows\":3}")), cancelled.Token));
    }
}
