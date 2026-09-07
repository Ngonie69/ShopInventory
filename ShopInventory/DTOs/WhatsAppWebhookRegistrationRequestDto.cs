using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

/// <summary>
/// The body sent to OpenWA to create or update a session webhook.
/// </summary>
/// <remarks>
/// Every optional field is omitted when null rather than sent as null. OpenWA runs its validation
/// pipe with <c>forbidNonWhitelisted</c>, so a property its DTO does not declare is a 400 even
/// when the value is null - and <c>active</c> is declared on the update DTO but not the create
/// one, which is exactly that case.
/// </remarks>
public class WhatsAppWebhookRegistrationRequestDto
{
    public string Url { get; set; } = string.Empty;

    public List<string> Events { get; set; } = [];

    /// <summary>
    /// HMAC-SHA256 secret. OpenWA signs each delivery as "sha256=&lt;hex&gt;" with it, which is
    /// exactly what <c>ReceiveOpenWAWebhookHandler</c> verifies against OpenWA:WebhookSecret.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Secret { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RetryCount { get; set; }

    /// <summary>
    /// Update only. OpenWA's create DTO does not accept it and defaults a new webhook to active.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Active { get; set; }
}
