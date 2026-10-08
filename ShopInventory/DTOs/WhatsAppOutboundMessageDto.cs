namespace ShopInventory.DTOs;

/// <summary>
/// One row of OpenWA's own message log, from <c>GET /api/sessions/{id}/messages</c>.
/// </summary>
/// <remarks>
/// OpenWA writes an outgoing row as <c>pending</c> before it hands the message to WhatsApp, then marks
/// it <c>sent</c> or <c>failed</c>; an unconfirmed send stays <c>pending</c>. That ordering is what lets
/// a send whose answer never came back be settled after the fact rather than guessed at. A document's
/// row keeps its file name in <see cref="Body"/>.
/// </remarks>
public sealed class WhatsAppOutboundMessageDto
{
    public string Id { get; set; } = string.Empty;

    public string? WaMessageId { get; set; }

    public string ChatId { get; set; } = string.Empty;

    public string? Body { get; set; }

    public string? Type { get; set; }

    /// <summary><c>incoming</c> or <c>outgoing</c>.</summary>
    public string? Direction { get; set; }

    /// <summary><c>pending</c>, <c>sent</c>, <c>delivered</c>, <c>read</c> or <c>failed</c>.</summary>
    public string? Status { get; set; }

    public DateTime? CreatedAt { get; set; }
}
