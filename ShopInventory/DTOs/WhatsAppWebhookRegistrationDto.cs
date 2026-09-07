namespace ShopInventory.DTOs;

/// <summary>
/// A webhook OpenWA holds for one session. Mirrors OpenWA's WebhookResponseDto, which
/// deliberately omits the shared secret, so the secret can never be read back for comparison.
/// </summary>
public class WhatsAppWebhookRegistrationDto
{
    public string Id { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public List<string> Events { get; set; } = [];

    public bool Active { get; set; }

    public int RetryCount { get; set; }

    public DateTime? LastTriggeredAt { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
