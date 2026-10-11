using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The document half of the OpenWA client. The gateway validates with <c>forbidNonWhitelisted</c>, so
/// the body must carry exactly the properties OpenWA declares — one extra, even a null one, refuses
/// the send, which is how webhook registration once broke on <c>active</c>.
/// </summary>
public sealed class OpenWAClientDocumentTests
{
    [Fact]
    public async Task A_document_is_posted_to_send_document_with_exactly_the_allowed_properties()
    {
        var handler = new RecordingHandler("""{"messageId":"true_263771234567@c.us_3EB0","timestamp":1706868000}""");
        var client = Client(handler);

        var result = await client.SendDocumentAsync("session-uuid", new WhatsAppSendDocumentRequestDto
        {
            ChatId = "263771234567@c.us",
            Base64 = "JVBERi0=",
            Mimetype = "application/pdf",
            Filename = "Kefalos-Invoice-780100.pdf",
            Caption = "Good day."
        });

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/sessions/session-uuid/messages/send-document", request.Path);
        Assert.Equal("key", request.ApiKey);

        using var body = JsonDocument.Parse(request.Body!);
        var names = body.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray();
        Assert.Equal(["base64", "caption", "chatId", "filename", "mimetype"], names);
        Assert.Equal("true_263771234567@c.us_3EB0", result.MessageId);
        Assert.Null(result.Unconfirmed);
    }

    [Fact]
    public async Task A_missing_caption_and_file_name_are_left_out_not_sent_as_null()
    {
        var handler = new RecordingHandler("""{"messageId":"x","timestamp":1}""");

        await Client(handler).SendDocumentAsync("s", new WhatsAppSendDocumentRequestDto
        {
            ChatId = "263771234567@c.us",
            Base64 = "JVBERi0="
        });

        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.False(body.RootElement.TryGetProperty("caption", out _));
        Assert.False(body.RootElement.TryGetProperty("filename", out _));
        Assert.False(body.RootElement.TryGetProperty("url", out _));
    }

    [Fact]
    public async Task An_unconfirmed_send_is_read_as_such()
    {
        var handler = new RecordingHandler("""{"messageId":null,"timestamp":1706868000,"unconfirmed":true}""");

        var result = await Client(handler).SendDocumentAsync("s", new WhatsAppSendDocumentRequestDto { ChatId = "c", Base64 = "b" });

        Assert.Null(result.MessageId);
        Assert.True(result.Unconfirmed);
    }

    [Fact]
    public async Task The_number_check_and_the_message_log_go_to_their_routes()
    {
        var handler = new RecordingHandler(
            """{"number":"263771234567","exists":true,"whatsappId":"263771234567@c.us"}""",
            """{"messages":[{"id":"1","waMessageId":null,"chatId":"263771234567@c.us","body":"Kefalos-Invoice-1.pdf","type":"document","direction":"outgoing","status":"pending","createdAt":"2026-10-08T06:00:00.000Z"}],"total":1}""");
        var client = Client(handler);

        var check = await client.CheckNumberAsync("s", "263771234567");
        var log = await client.GetMessagesAsync("s", "263771234567@c.us", 50);

        Assert.True(check.Exists);
        Assert.Equal("/api/sessions/s/contacts/check/263771234567", handler.Requests[0].Path);
        Assert.Equal("/api/sessions/s/messages?chatId=263771234567%40c.us&limit=50", handler.Requests[1].PathAndQuery);
        var row = Assert.Single(log.Messages);
        Assert.Equal("pending", row.Status);
        Assert.Equal("Kefalos-Invoice-1.pdf", row.Body);
        Assert.Equal(DateTimeKind.Utc, row.CreatedAt!.Value.Kind);
    }

    [Fact]
    public async Task A_gateway_refusal_carries_its_status_and_the_message_from_its_array()
    {
        var handler = new RecordingHandler(HttpStatusCode.BadRequest,
            """{"message":["property url should not exist"],"error":"Bad Request","statusCode":400}""");

        var refusal = await Assert.ThrowsAsync<OpenWAGatewayException>(() =>
            Client(handler).SendDocumentAsync("s", new WhatsAppSendDocumentRequestDto { ChatId = "c", Base64 = "b" }));

        Assert.Equal(HttpStatusCode.BadRequest, refusal.StatusCode);
        Assert.Contains("property url should not exist", refusal.Message);
    }

    [Fact]
    public async Task A_document_gets_the_longer_deadline_and_other_calls_keep_the_short_one()
    {
        var settings = new OpenWASettings { ApiKey = "key", TimeoutSeconds = 1, DocumentTimeoutSeconds = 4 };

        var slowDocument = new RecordingHandler("""{"messageId":"x","timestamp":1}""") { Delay = TimeSpan.FromSeconds(2) };
        var sent = await Client(slowDocument, settings)
            .SendDocumentAsync("s", new WhatsAppSendDocumentRequestDto { ChatId = "c", Base64 = "b" });
        Assert.Equal("x", sent.MessageId);

        // Never answers, and HttpClient's own timeout is off, so the call can end only by the client's
        // deadline. This used to be an answer after two seconds against the one-second deadline. Both
        // are timers, and on a busy runner the answer sometimes came first: no exception, and a red
        // build with nothing wrong. The wait is not the test; it turns a deadline that never fires into
        // a failure instead of a run that hangs.
        var neverAnswers = new RecordingHandler("""{"number":"1","exists":true}""") { Delay = Timeout.InfiniteTimeSpan };
        var timeout = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            Client(neverAnswers, settings, Timeout.InfiniteTimeSpan)
                .CheckNumberAsync("s", "263771234567")
                .WaitAsync(TimeSpan.FromSeconds(60)));

        // Reported as HttpClient reports its own timeout, so callers tell it from their own cancellation.
        Assert.IsType<TimeoutException>(timeout.InnerException);

        // The short deadline, not the document's.
        Assert.Contains("within 1 seconds", timeout.Message);
    }

    private static OpenWAClient Client(
        RecordingHandler handler, OpenWASettings? settings = null, TimeSpan? httpTimeout = null)
    {
        var configured = settings ?? new OpenWASettings { ApiKey = "key", TimeoutSeconds = 30, DocumentTimeoutSeconds = 60 };
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:2785"),
            Timeout = httpTimeout
                ?? TimeSpan.FromSeconds(Math.Max(configured.TimeoutSeconds, configured.DocumentTimeoutSeconds) + 5)
        };

        return new OpenWAClient(http, Options.Create(configured), NullLogger<OpenWAClient>.Instance);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _answers = new();

        public RecordingHandler(params string[] bodies)
        {
            foreach (var body in bodies)
                _answers.Enqueue((HttpStatusCode.OK, body));
        }

        public RecordingHandler(HttpStatusCode status, string body) => _answers.Enqueue((status, body));

        public List<RecordedRequest> Requests { get; } = [];

        /// <summary>How long before it answers. <see cref="Timeout.InfiniteTimeSpan"/> never answers.</summary>
        public TimeSpan Delay { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.RequestUri.PathAndQuery,
                request.Headers.TryGetValues("X-API-Key", out var keys) ? keys.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

            if (Delay != TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);

            var (status, body) = _answers.Count > 1 ? _answers.Dequeue() : _answers.Peek();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string PathAndQuery, string? ApiKey, string? Body);
}
