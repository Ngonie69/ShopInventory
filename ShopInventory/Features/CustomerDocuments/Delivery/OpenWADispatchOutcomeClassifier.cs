using System.Net;
using System.Net.Http;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments.Delivery;

/// <summary>
/// Sorts what came back from a document send into what may be retried and what may not.
/// </summary>
/// <remarks>
/// <para>
/// The line that matters is whether the message can have reached WhatsApp. A send can be retried only
/// when it provably did not: the gateway refused it before the send (session not active, too many
/// requests, the engine not ready - a 503 from Ngonie69/OpenWA#2 on), or the connection was never made.
/// </para>
/// <para>
/// Anything after the request may have arrived — a timeout, a 500, a reply cut short — is
/// <see cref="OpenWADispatchOutcomeKind.Uncertain"/>. Retrying that would send the customer a second
/// copy whenever the first did go, so it is settled from the gateway's own log instead. A gateway
/// older than OpenWA#2 answers 500 for an engine that is not ready, which lands here too; the log then
/// shows it failed, and it is failed rather than resent.
/// </para>
/// </remarks>
public static class OpenWADispatchOutcomeClassifier
{
    public const string SessionDownAlert = "SessionDown";
    public const string GatewayRefusedAlert = "GatewayRefused";
    public const string DocumentTooLargeAlert = "DocumentTooLarge";

    public static OpenWADispatchOutcome Classify(WhatsAppMessageDispatchDto result)
    {
        if (result.Unconfirmed == true || string.IsNullOrWhiteSpace(result.MessageId))
        {
            return new OpenWADispatchOutcome(OpenWADispatchOutcomeKind.SentUnconfirmed, null, result.Timestamp, null);
        }

        return new OpenWADispatchOutcome(OpenWADispatchOutcomeKind.Sent, result.MessageId, result.Timestamp, null);
    }

    public static OpenWADispatchOutcome Classify(Exception exception)
    {
        switch (exception)
        {
            case OpenWAGatewayException gateway:
                return ClassifyGatewayRefusal(gateway);

            case HttpRequestException transport
                when transport.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError:
                return new OpenWADispatchOutcome(
                    OpenWADispatchOutcomeKind.NotSent, null, null,
                    $"OpenWA could not be reached: {transport.Message}");

            case OperationCanceledException:
                return new OpenWADispatchOutcome(
                    OpenWADispatchOutcomeKind.Uncertain, null, null,
                    $"OpenWA did not answer in time: {exception.Message}");

            default:
                return new OpenWADispatchOutcome(
                    OpenWADispatchOutcomeKind.Uncertain, null, null,
                    $"The answer from OpenWA was lost: {exception.Message}");
        }
    }

    private static OpenWADispatchOutcome ClassifyGatewayRefusal(OpenWAGatewayException gateway)
    {
        var status = (int)gateway.StatusCode;
        var message = $"OpenWA answered {status}: {gateway.Message}";

        if (gateway.StatusCode == HttpStatusCode.BadRequest
            && gateway.Message.Contains("is not active", StringComparison.OrdinalIgnoreCase))
        {
            // MessageService.getEngine refuses before it writes or sends anything.
            return new OpenWADispatchOutcome(OpenWADispatchOutcomeKind.NotSent, null, null, message, SessionDownAlert);
        }

        return gateway.StatusCode switch
        {
            HttpStatusCode.ServiceUnavailable =>
                new OpenWADispatchOutcome(OpenWADispatchOutcomeKind.NotSent, null, null, message, SessionDownAlert),
            HttpStatusCode.TooManyRequests =>
                new OpenWADispatchOutcome(OpenWADispatchOutcomeKind.NotSent, null, null, message),
            HttpStatusCode.RequestEntityTooLarge =>
                new OpenWADispatchOutcome(OpenWADispatchOutcomeKind.Rejected, null, null, message, DocumentTooLargeAlert),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound =>
                new OpenWADispatchOutcome(OpenWADispatchOutcomeKind.Paused, null, null, message, GatewayRefusedAlert),
            _ when status >= 500 =>
                new OpenWADispatchOutcome(OpenWADispatchOutcomeKind.Uncertain, null, null, message),
            _ =>
                // Any other refusal is about this request — a property the gateway does not accept, a
                // malformed number — and would be refused again.
                new OpenWADispatchOutcome(OpenWADispatchOutcomeKind.Rejected, null, null, message, GatewayRefusedAlert)
        };
    }
}
