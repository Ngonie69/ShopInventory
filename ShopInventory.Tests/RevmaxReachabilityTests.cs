using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// What the REVMax client does when the device stops answering.
/// </summary>
/// <remarks>
/// It waited 90 seconds per read and retried a timeout three more times, six minutes per lookup, and
/// the jobs that fiscalise kept asking for the next document. On a hung device that held job threads
/// for hours.
/// </remarks>
public sealed class RevmaxReachabilityTests
{
    private readonly FakeDevice _device = new();
    private readonly ManualClock _clock = new();
    private readonly RevmaxReachability _reachability;
    private readonly RevmaxClient _client;

    public RevmaxReachabilityTests()
    {
        _reachability = new RevmaxReachability(_clock);
        _client = new RevmaxClient(
            new HttpClient(_device),
            Options.Create(new RevmaxSettings
            {
                BaseUrl = "http://revmax.invalid:8001",
                TimeoutSeconds = 1,
                MaxRetries = 3,
                RetryDelaysMs = [1, 1, 1]
            }),
            NullLogger<RevmaxClient>.Instance,
            _reachability);
    }

    [Fact]
    public async Task A_read_the_device_never_answers_is_sent_once_not_retried()
    {
        _device.Hangs = true;

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => _client.GetInvoiceAsync("771485"));

        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Equal(1, _device.Reads);
    }

    [Fact]
    public async Task Reads_after_the_device_stops_answering_fail_at_once_until_the_cooldown_ends()
    {
        _device.Hangs = true;
        await Assert.ThrowsAsync<TaskCanceledException>(() => _client.GetInvoiceAsync("771485"));

        // Refused without being sent.
        await Assert.ThrowsAsync<RevmaxUnreachableException>(() => _client.GetInvoiceAsync("771486"));
        await Assert.ThrowsAsync<RevmaxUnreachableException>(() => _client.GetDayStatusAsync());
        Assert.Equal(1, _device.Reads);

        _device.Hangs = false;
        _clock.Advance(RevmaxReachability.Cooldown + TimeSpan.FromSeconds(1));

        Assert.NotNull(await _client.GetInvoiceAsync("771486"));
        Assert.Equal(2, _device.Reads);
    }

    [Fact]
    public async Task A_device_that_refuses_connections_is_treated_the_same_way()
    {
        _device.RefusesConnections = true;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _client.GetInvoiceAsync("771485"));
        Assert.True(RevmaxReachability.IsNoAnswer(ex));

        // A refused connection is quick, so it keeps its short retries; after them, reads stop.
        Assert.Equal(4, _device.Reads);
        await Assert.ThrowsAsync<RevmaxUnreachableException>(() => _client.GetInvoiceAsync("771486"));
        Assert.Equal(4, _device.Reads);
    }

    [Fact]
    public async Task Any_answer_from_the_device_ends_the_refusal()
    {
        _reachability.MarkUnreachable();
        _clock.Advance(RevmaxReachability.Cooldown + TimeSpan.FromSeconds(1));
        _device.Status = HttpStatusCode.InternalServerError;

        // The 500 is an answer: the device is there, so reads are not refused after it.
        await Assert.ThrowsAsync<HttpRequestException>(() => _client.GetInvoiceAsync("771485"));
        Assert.Null(_reachability.RefusingUntil);
    }

    [Fact]
    public async Task A_receipt_submission_is_never_refused()
    {
        // Refusing one would read to the caller as a submission whose outcome is unknown.
        _reachability.MarkUnreachable();

        await _client.TransactMAsync(new TransactMRequest());

        Assert.Equal(1, _device.Submissions);
        Assert.Null(_reachability.RefusingUntil);
    }

    [Fact]
    public void Only_a_missing_answer_counts_not_an_answer_the_caller_disliked()
    {
        Assert.True(RevmaxReachability.IsNoAnswer(
            new InvalidOperationException("REVMax could not be asked.", new HttpRequestException("refused"))));
        Assert.True(RevmaxReachability.IsNoAnswer(
            new TaskCanceledException("timed out", new TimeoutException())));

        Assert.False(RevmaxReachability.IsNoAnswer(
            new HttpRequestException("Internal Server Error", null, HttpStatusCode.InternalServerError)));
        Assert.False(RevmaxReachability.IsNoAnswer(
            new InvalidOperationException("REVMax did not say whether it already holds this receipt (Code 0: Init error -1).")));
        Assert.False(RevmaxReachability.IsNoAnswer(new TaskCanceledException("the caller gave up")));
    }

    /// <summary>A REVMax box that answers, hangs or refuses connections on demand.</summary>
    private sealed class FakeDevice : HttpMessageHandler
    {
        private int _reads;
        private int _submissions;

        public bool Hangs { get; set; }
        public bool RefusesConnections { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public int Reads => Volatile.Read(ref _reads);
        public int Submissions => Volatile.Read(ref _submissions);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref _submissions);
                return Json("{\"Code\":\"1\",\"Message\":\"Success\"}");
            }

            Interlocked.Increment(ref _reads);

            if (RefusesConnections)
                throw new HttpRequestException("No connection could be made because the target machine actively refused it.");

            if (Hangs)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return Json("{\"Code\":\"0\",\"Message\":\"Invoice not Found\",\"Data\":\"\"}", Status);
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
