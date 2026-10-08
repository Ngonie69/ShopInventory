namespace ShopInventory.DTOs;

public class WhatsAppMessageDispatchDto
{
    /// <summary>
    /// WhatsApp's id for the message. Null when the send is <see cref="Unconfirmed"/>.
    /// </summary>
    public string? MessageId { get; set; }

    public long Timestamp { get; set; }

    /// <summary>
    /// True when WhatsApp accepted the send but returned no message to confirm it by — which current
    /// WhatsApp Web builds often do for a message that was delivered. It has very likely arrived, so it
    /// must never be retried on this alone: the recipient would get it twice.
    /// </summary>
    public bool? Unconfirmed { get; set; }
}
