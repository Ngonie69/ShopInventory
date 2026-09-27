using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The desktop integration reads beyond the sales list, which share its failure wording through ReadAsync.
/// </summary>
/// <remarks>
/// Each used to return null for any failure, so Desktop Transactions showed an outage as empty queues and
/// Local Stock showed it as "no snapshot found". <see cref="DesktopSalesLoadFailureTests"/> pins the wording
/// itself; these pin what is particular to the other reads.
/// </remarks>
public sealed class DesktopIntegrationReadFailureTests
{
    [Fact]
    public async Task A_queue_read_that_fails_says_why_instead_of_reading_as_empty()
    {
        var service = Service(Answer(HttpStatusCode.InternalServerError,
            """{"title":"Internal Server Error","status":500,"detail":"The queue table could not be read"}""",
            "application/problem+json"));

        var (stats, error) = await service.GetQueueStatsAsync();

        Assert.Null(stats);
        Assert.Equal("The API answered 500 Internal Server Error: The queue table could not be read.", error);
    }

    [Fact]
    public async Task A_day_with_no_snapshot_is_an_answer_not_an_error()
    {
        // What GetLocalStockHandler sends through Problem() for SnapshotNotFound.
        var service = Service(Answer(HttpStatusCode.NotFound,
            """{"title":"Not Found","status":404,"detail":"No stock snapshot found for warehouse 'KEFBYC' on 2026-09-27"}""",
            "application/problem+json"));

        var (snapshot, error) = await service.GetLocalStockAsync("KEFBYC", new DateTime(2026, 9, 27));

        Assert.Null(snapshot);
        Assert.Null(error);
    }

    [Fact]
    public async Task A_bare_404_is_a_missing_route_and_is_reported()
    {
        // No problem body: nothing in the API answered, so this is the Web calling a route that does not
        // exist. Read as "no snapshot", a renamed route would hide for as long as nobody looked closely.
        var service = Service(Answer(HttpStatusCode.NotFound, "", "text/plain"));

        var (snapshot, error) = await service.GetLocalStockAsync("KEFBYC");

        Assert.Null(snapshot);
        Assert.Equal("The API answered 404 Not Found, with no explanation.", error);
    }

    [Fact]
    public async Task A_404_elsewhere_is_an_error_even_with_a_problem_body()
    {
        // Only the snapshot read treats not-found as empty. A queue that 404s is not an empty queue.
        var service = Service(Answer(HttpStatusCode.NotFound,
            """{"title":"Not Found","status":404,"detail":"Nothing here"}""", "application/problem+json"));

        var (queue, error) = await service.GetPendingQueueAsync();

        Assert.Null(queue);
        Assert.Equal("The API answered 404 Not Found: Nothing here.", error);
    }

    [Fact]
    public async Task Reservations_are_unwrapped_from_their_page()
    {
        var service = Service(Answer(HttpStatusCode.OK,
            """{"reservations":[{"reservationId":"R-1"}],"totalCount":1}""", "application/json"));

        var (reservations, error) = await service.GetReservationsAsync();

        Assert.Null(error);
        Assert.Equal("R-1", Assert.Single(reservations!).ReservationId);
    }

    [Fact]
    public async Task Reservations_that_fail_say_why()
    {
        var service = Service(Answer(HttpStatusCode.ServiceUnavailable, "<html>503</html>", "text/html"));

        var (reservations, error) = await service.GetReservationsAsync();

        Assert.Null(reservations);
        Assert.Equal("The API answered 503 Service Unavailable, with no explanation.", error);
    }

    private static DesktopIntegrationService Service(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") },
            NullLogger<DesktopIntegrationService>.Instance);

    private static HttpMessageHandler Answer(HttpStatusCode status, string body, string mediaType) =>
        new FixedHandler(status, body, mediaType);

    private sealed class FixedHandler(HttpStatusCode status, string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            });
    }
}
