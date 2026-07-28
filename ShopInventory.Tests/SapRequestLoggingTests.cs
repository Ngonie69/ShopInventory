using System.Net;
using ShopInventory.Middleware;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The handler's whole job is to hand a row off and get out of the way: it runs innermost, so
/// anything it waits on is waited on while the caller holds a SAP concurrency slot.
/// </summary>
public class SapRequestLoggingHandlerTests
{
    [Fact]
    public async Task Successful_request_is_queued_with_its_endpoint_and_duration()
    {
        var queue = new SapRequestLogQueue();
        using var invoker = CreateInvoker(queue, _ => new HttpResponseMessage(HttpStatusCode.OK));

        await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://sap.invalid/b1s/v1/Orders?$top=1"),
            CancellationToken.None);

        var entry = DequeueSingle(queue);
        Assert.True(entry.IsSuccess);
        Assert.Equal("GET Orders", entry.Endpoint);
        Assert.Null(entry.ErrorMessage);
        Assert.NotNull(entry.ResponseTimeMs);
    }

    [Fact]
    public async Task Failed_status_is_queued_as_a_failure()
    {
        var queue = new SapRequestLogQueue();
        using var invoker = CreateInvoker(
            queue,
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { ReasonPhrase = "Bad Gateway" });

        await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "https://sap.invalid/b1s/v1/SQLQueries"),
            CancellationToken.None);

        var entry = DequeueSingle(queue);
        Assert.False(entry.IsSuccess);
        Assert.Equal("POST SQLQueries", entry.Endpoint);
        Assert.Equal("502 Bad Gateway", entry.ErrorMessage);
    }

    [Fact]
    public async Task Thrown_transport_failure_is_queued_and_still_propagates()
    {
        var queue = new SapRequestLogQueue();
        using var invoker = CreateInvoker(
            queue,
            _ => throw new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://sap.invalid/b1s/v1/Orders"),
            CancellationToken.None));

        var entry = DequeueSingle(queue);
        Assert.False(entry.IsSuccess);
        Assert.Equal("connection refused", entry.ErrorMessage);
    }

    [Fact]
    public async Task Recording_does_not_wait_on_the_writer()
    {
        // Nothing drains the queue here. If recording ever went back to awaiting a consumer — a
        // database write, say — this would hang rather than fail.
        var queue = new SapRequestLogQueue();
        using var invoker = CreateInvoker(queue, _ => new HttpResponseMessage(HttpStatusCode.OK));

        var send = invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://sap.invalid/b1s/v1/Orders"),
            CancellationToken.None);

        await send.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static SapConnectionLog DequeueSingle(SapRequestLogQueue queue)
    {
        Assert.True(queue.TryDequeue(out var entry), "Expected one queued log entry.");
        Assert.False(queue.TryDequeue(out _), "Expected exactly one queued log entry.");
        return entry;
    }

    private static HttpMessageInvoker CreateInvoker(
        SapRequestLogQueue queue,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new SAPRequestLoggingHandler(queue)
        {
            InnerHandler = new StubHandler(respond)
        };

        return new HttpMessageInvoker(handler);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}

public class SapRequestLogQueueTests
{
    [Fact]
    public void Overflow_drops_the_oldest_entries_rather_than_blocking_the_producer()
    {
        // The table keeps the most recent 100 rows, so under pressure the newest entries are the
        // ones worth keeping — and a SAP call must never wait for room.
        var queue = new SapRequestLogQueue();

        for (var i = 0; i < 5_000; i++)
        {
            queue.TryEnqueue(new SapConnectionLog { Endpoint = $"GET Orders/{i}" });
        }

        var drained = new List<SapConnectionLog>();
        while (queue.TryDequeue(out var entry))
        {
            drained.Add(entry);
        }

        Assert.NotEmpty(drained);
        // Whatever survived must be the tail of what was written, not the head.
        Assert.Equal("GET Orders/4999", drained[^1].Endpoint);
    }
}
