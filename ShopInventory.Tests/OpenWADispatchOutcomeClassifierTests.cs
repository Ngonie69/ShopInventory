using System.Net;
using System.Net.Http;
using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments.Delivery;

namespace ShopInventory.Tests;

/// <summary>
/// The line between what may be retried and what may not. A send may be retried only when it provably
/// never left; anything after the request may have arrived must be settled from the gateway's log, or
/// the customer gets a second copy every time the first did go.
/// </summary>
public sealed class OpenWADispatchOutcomeClassifierTests
{
    [Fact]
    public void An_answer_with_a_message_id_is_sent()
    {
        var outcome = OpenWADispatchOutcomeClassifier.Classify(new WhatsAppMessageDispatchDto { MessageId = "wamid", Timestamp = 5 });

        Assert.Equal(OpenWADispatchOutcomeKind.Sent, outcome.Kind);
        Assert.Equal("wamid", outcome.MessageId);
        Assert.Equal(5, outcome.GatewayTimestamp);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", null)]
    [InlineData("wamid", true)]
    public void An_answer_without_an_id_or_marked_unconfirmed_is_sent_unconfirmed(string? messageId, bool? unconfirmed)
    {
        var outcome = OpenWADispatchOutcomeClassifier.Classify(new WhatsAppMessageDispatchDto { MessageId = messageId, Unconfirmed = unconfirmed });

        Assert.Equal(OpenWADispatchOutcomeKind.SentUnconfirmed, outcome.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "Session 'abc' is not active. Start the session first.", OpenWADispatchOutcomeKind.NotSent)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Engine not ready", OpenWADispatchOutcomeKind.NotSent)]
    [InlineData(HttpStatusCode.TooManyRequests, "ThrottlerException: Too Many Requests", OpenWADispatchOutcomeKind.NotSent)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "request entity too large", OpenWADispatchOutcomeKind.Rejected)]
    [InlineData(HttpStatusCode.BadRequest, "property url should not exist", OpenWADispatchOutcomeKind.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, "Invalid API key", OpenWADispatchOutcomeKind.Paused)]
    [InlineData(HttpStatusCode.Forbidden, "Insufficient role", OpenWADispatchOutcomeKind.Paused)]
    [InlineData(HttpStatusCode.NotFound, "Session not found", OpenWADispatchOutcomeKind.Paused)]
    [InlineData(HttpStatusCode.InternalServerError, "Evaluation failed", OpenWADispatchOutcomeKind.Uncertain)]
    [InlineData(HttpStatusCode.BadGateway, "upstream", OpenWADispatchOutcomeKind.Uncertain)]
    public void A_gateway_refusal_is_sorted_by_whether_the_message_can_have_left(
        HttpStatusCode status, string message, OpenWADispatchOutcomeKind expected)
    {
        var outcome = OpenWADispatchOutcomeClassifier.Classify(FakeOpenWAClient.Refusal(status, message));

        Assert.Equal(expected, outcome.Kind);
        Assert.Contains(message, outcome.Error);
    }

    [Fact]
    public void Refusals_that_need_an_administrator_say_so()
    {
        Assert.Equal(OpenWADispatchOutcomeClassifier.SessionDownAlert,
            OpenWADispatchOutcomeClassifier.Classify(FakeOpenWAClient.Refusal(HttpStatusCode.ServiceUnavailable, "x")).AlertCondition);
        Assert.Equal(OpenWADispatchOutcomeClassifier.DocumentTooLargeAlert,
            OpenWADispatchOutcomeClassifier.Classify(FakeOpenWAClient.Refusal(HttpStatusCode.RequestEntityTooLarge, "x")).AlertCondition);
        Assert.Equal(OpenWADispatchOutcomeClassifier.GatewayRefusedAlert,
            OpenWADispatchOutcomeClassifier.Classify(FakeOpenWAClient.Refusal(HttpStatusCode.Unauthorized, "x")).AlertCondition);
        Assert.Null(
            OpenWADispatchOutcomeClassifier.Classify(FakeOpenWAClient.Refusal(HttpStatusCode.TooManyRequests, "x")).AlertCondition);
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError, OpenWADispatchOutcomeKind.NotSent)]
    [InlineData(HttpRequestError.NameResolutionError, OpenWADispatchOutcomeKind.NotSent)]
    [InlineData(HttpRequestError.ResponseEnded, OpenWADispatchOutcomeKind.Uncertain)]
    [InlineData(HttpRequestError.Unknown, OpenWADispatchOutcomeKind.Uncertain)]
    public void A_connection_never_made_is_not_sent_but_a_reply_cut_short_is_uncertain(
        HttpRequestError error, OpenWADispatchOutcomeKind expected)
    {
        var outcome = OpenWADispatchOutcomeClassifier.Classify(new HttpRequestException(error, "transport"));

        Assert.Equal(expected, outcome.Kind);
    }

    [Fact]
    public void A_timeout_is_uncertain()
    {
        var outcome = OpenWADispatchOutcomeClassifier.Classify(new TaskCanceledException("late", new TimeoutException()));

        Assert.Equal(OpenWADispatchOutcomeKind.Uncertain, outcome.Kind);
    }
}
