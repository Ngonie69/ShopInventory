using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CustomerDocuments.Delivery;

/// <summary>
/// Settles a send whose answer was lost, from the gateway's own record of it.
/// </summary>
/// <remarks>
/// <para>
/// OpenWA writes an outgoing message to its log as <c>pending</c> before it hands it to WhatsApp, then
/// marks it <c>sent</c> or <c>failed</c>; a send WhatsApp accepted without an id stays <c>pending</c>.
/// A document's row carries its file name. So for a send that went quiet, the log says what happened:
/// </para>
/// <list type="bullet">
/// <item>a sent (or delivered, or read) row — it went;</item>
/// <item>a pending row — WhatsApp took it without confirming, as it so often does: treated as sent;</item>
/// <item>a failed row — it did not go, and a person decides whether to resend;</item>
/// <item>no row at all — the request never reached the point of sending, so it can safely be tried again.</item>
/// </list>
/// <para>
/// Matched on the chat, the file name and a window around when the send was issued, so an earlier copy
/// of the same invoice sent to the same number on purpose is not mistaken for this one.
/// </para>
/// </remarks>
public static class UncertainDeliveryReconciler
{
    public static UncertainDeliveryDecision Decide(
        CustomerDocumentDeliveryEntity delivery,
        IReadOnlyCollection<WhatsAppOutboundMessageDto> log,
        TimeSpan documentTimeout)
    {
        if (delivery.SendIssuedAtUtc is not { } issued)
        {
            return new UncertainDeliveryDecision(CustomerDocumentDeliveryStatus.Pending, null,
                "No send was recorded, so it can be tried again.");
        }

        var from = issued.AddMinutes(-2);
        var to = issued.Add(documentTimeout).AddMinutes(5);
        var chatId = WhatsAppRecipients.ChatId(delivery.RecipientE164);

        var matches = log
            .Where(message => string.Equals(message.Direction, "outgoing", StringComparison.OrdinalIgnoreCase)
                && string.Equals(message.ChatId, chatId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(message.Body, delivery.FileName, StringComparison.Ordinal)
                && message.CreatedAt is { } created
                && ToUtc(created) >= from
                && ToUtc(created) <= to)
            .ToList();

        var sent = matches.FirstOrDefault(message => IsAny(message.Status, "sent", "delivered", "read"));
        if (sent is not null)
        {
            return new UncertainDeliveryDecision(CustomerDocumentDeliveryStatus.Sent, sent.WaMessageId,
                "The gateway's log shows it was sent.");
        }

        if (matches.Any(message => IsAny(message.Status, "pending")))
        {
            return new UncertainDeliveryDecision(CustomerDocumentDeliveryStatus.SentUnconfirmed, null,
                "The gateway's log shows WhatsApp took it without confirming it.");
        }

        if (matches.Any(message => IsAny(message.Status, "failed")))
        {
            return new UncertainDeliveryDecision(CustomerDocumentDeliveryStatus.Failed, null,
                "The gateway's log shows the send failed. Resend it if the customer still needs it.");
        }

        return new UncertainDeliveryDecision(CustomerDocumentDeliveryStatus.Pending, null,
            "The gateway never recorded the send, so it is being tried again.");
    }

    private static bool IsAny(string? value, params string[] candidates) =>
        candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
