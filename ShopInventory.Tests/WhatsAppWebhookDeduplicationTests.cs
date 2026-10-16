using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Tests;

/// <summary>
/// An OpenWA webhook is deduplicated on what the event is, not on the key OpenWA sends.
/// </summary>
/// <remarks>
/// On 2026-10-14 the gateway logged bursts of "Webhook delivery failed ... HTTP 409". OpenWA keys a
/// <c>message.received</c> as <c>msg_{data.messageId}</c>, but a received message carries
/// <c>data.id</c>, so every message on every session arrived as <c>msg_unknown</c>. The handler
/// fingerprinted the whole body, which differs on every dispatch, so each new message under that
/// key was a mismatch: 409, three retries, dropped. Once the claim expired the next message was
/// answered with the first message's receipt instead, and was not stored either.
/// </remarks>
public sealed class WhatsAppWebhookDeduplicationTests : IDisposable
{
    private const string SessionId = "3270941b-0000-4000-8000-000000000001";

    private readonly WhatsAppWebhookHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Two_messages_under_openwas_shared_key_are_both_stored()
    {
        var first = await _harness.Receive(Message("false_263771234567@c.us_3EB0A1", "First", "dlv_1"), "msg_unknown", "dlv_1");
        var second = await _harness.Receive(Message("false_263771234567@c.us_3EB0A2", "Second", "dlv_2"), "msg_unknown", "dlv_2");

        Assert.False(first.IsError, Describe(first));
        Assert.False(second.IsError, Describe(second));
        Assert.NotEqual(first.Value.Id, second.Value.Id);
        Assert.Equal(["First", "Second"], await StoredBodies());
    }

    [Fact]
    public async Task A_second_message_after_the_claim_expired_is_stored_not_answered_with_the_first()
    {
        var first = await _harness.Receive(Message("false_263771234567@c.us_3EB0B1", "First", "dlv_1"), "msg_unknown", "dlv_1");
        await ExpireClaims();

        var second = await _harness.Receive(Message("false_263771234567@c.us_3EB0B2", "Second", "dlv_2"), "msg_unknown", "dlv_2");

        Assert.False(second.IsError, Describe(second));
        Assert.NotEqual(first.Value.Id, second.Value.Id);
        Assert.Equal(["First", "Second"], await StoredBodies());
    }

    [Fact]
    public async Task A_message_dispatched_again_replays_its_first_receipt()
    {
        const string messageId = "false_263771234567@c.us_3EB0C1";

        var first = await _harness.Receive(Message(messageId, "Hello", "dlv_1", "2026-10-14T11:00:00.000Z"), "msg_unknown", "dlv_1");
        var again = await _harness.Receive(Message(messageId, "Hello", "dlv_2", "2026-10-14T11:00:07.000Z"), "msg_unknown", "dlv_2");

        Assert.False(again.IsError, Describe(again));
        Assert.Equal(first.Value.Id, again.Value.Id);
        Assert.Equal(["Hello"], await StoredBodies());
    }

    [Fact]
    public async Task A_retry_of_one_dispatch_replays_its_receipt()
    {
        var payload = Message("false_263771234567@c.us_3EB0D1", "Hello", "dlv_1");

        var first = await _harness.Receive(payload, "msg_unknown", "dlv_1");
        var retry = await _harness.Receive(payload, "msg_unknown", "dlv_1");

        Assert.False(retry.IsError, Describe(retry));
        Assert.Equal(first.Value.Id, retry.Value.Id);
        Assert.Equal(["Hello"], await StoredBodies());
    }

    [Fact]
    public async Task The_same_message_id_on_two_sessions_is_two_messages()
    {
        const string groupMessageId = "false_120363000000000000@g.us_3EB0E1_263771234567@c.us";

        var first = await _harness.Receive(Message(groupMessageId, "To the group", "dlv_1"), "msg_unknown", "dlv_1");
        var second = await _harness.Receive(Message(groupMessageId, "To the group", "dlv_2", session: "fiscal-alerts"), "msg_unknown", "dlv_2");

        Assert.False(second.IsError, Describe(second));
        Assert.NotEqual(first.Value.Id, second.Value.Id);
    }

    [Fact]
    public async Task A_session_returning_to_a_status_it_had_before_is_a_new_event()
    {
        var ready = await _harness.Receive(SessionStatus("ready", "dlv_1"), $"sess_{SessionId}_ready", "dlv_1");
        var disconnected = await _harness.Receive(SessionStatus("disconnected", "dlv_2"), $"sess_{SessionId}_disconnected", "dlv_2");
        var readyAgain = await _harness.Receive(SessionStatus("ready", "dlv_3"), $"sess_{SessionId}_ready", "dlv_3");

        Assert.False(readyAgain.IsError, Describe(readyAgain));
        Assert.Equal(3, new[] { ready.Value.Id, disconnected.Value.Id, readyAgain.Value.Id }.Distinct().Count());

        using var context = _harness.NewContext();
        Assert.Equal(3, await context.WhatsAppWebhookEvents.CountAsync(item => item.EventType == "session.status"));
    }

    [Fact]
    public async Task A_message_with_a_very_long_id_is_stored_under_a_key_that_fits()
    {
        var longId = "false_120363000000000000@g.us_" + new string('A', 300);

        var result = await _harness.Receive(Message(longId, "Long", "dlv_1"), "msg_unknown", "dlv_1");

        Assert.False(result.IsError, Describe(result));
        using var context = _harness.NewContext();
        var stored = await context.WhatsAppWebhookEvents.SingleAsync();
        Assert.True(stored.IdempotencyKey!.Length <= 200, stored.IdempotencyKey);
    }

    /// <summary>The body OpenWA's WebhookService posts for a received message.</summary>
    private static string Message(string messageId, string body, string deliveryId, string timestamp = "2026-10-14T11:00:00.000Z", string session = SessionId)
        => $$$"""
            {"event":"message.received","timestamp":"{{{timestamp}}}","sessionId":"{{{session}}}","idempotencyKey":"msg_unknown","deliveryId":"{{{deliveryId}}}","data":{"id":"{{{messageId}}}","from":"263771234567@c.us","to":"263717068336@c.us","chatId":"263771234567@c.us","body":"{{{body}}}","type":"chat","timestamp":1760439600,"fromMe":false,"isGroup":false}}
            """;

    private static string SessionStatus(string status, string deliveryId)
        => $$$"""
            {"event":"session.status","timestamp":"2026-10-14T11:00:00.000Z","sessionId":"{{{SessionId}}}","idempotencyKey":"sess_{{{SessionId}}}_{{{status}}}","deliveryId":"{{{deliveryId}}}","data":{"status":"{{{status}}}"}}
            """;

    private async Task<List<string?>> StoredBodies()
    {
        using var context = _harness.NewContext();
        return await context.WhatsAppWebhookEvents.OrderBy(item => item.Id).Select(item => item.TextBody).ToListAsync();
    }

    private async Task ExpireClaims()
    {
        using var context = _harness.NewContext();
        await context.IdempotencyRequests.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
    }

    private static string Describe<T>(ErrorOr.ErrorOr<T> result)
        => result.IsError ? string.Join("; ", result.Errors.Select(error => $"{error.Code}: {error.Description}")) : "ok";
}
