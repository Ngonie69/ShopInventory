namespace ShopInventory.DTOs;

/// <summary>
/// What the console needs to know about the delivery path for one session: whether OpenWA is
/// holding a webhook that points back at this API, and what was done to make that true.
/// </summary>
public class WhatsAppWebhookStatusDto
{
    /// <summary>
    /// True when OpenWA holds an active webhook aimed at OpenWA:WebhookPublicUrl. False means
    /// inbound messages will never reach the inbox, whatever the session's own status says.
    /// </summary>
    public bool Registered { get; set; }

    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// The URL this API expects OpenWA to deliver to.
    /// </summary>
    public string ExpectedUrl { get; set; } = string.Empty;

    public string? WebhookId { get; set; }

    public List<string> Events { get; set; } = [];

    /// <summary>
    /// "created", "updated", "unchanged", or "failed".
    /// </summary>
    public string Action { get; set; } = "unchanged";

    /// <summary>
    /// Operator-facing explanation. Always populated when <see cref="Registered"/> is false.
    /// </summary>
    public string? Message { get; set; }

    public DateTime? LastTriggeredAtUtc { get; set; }
}
