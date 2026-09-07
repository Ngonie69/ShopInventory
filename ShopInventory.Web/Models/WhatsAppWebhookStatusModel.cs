namespace ShopInventory.Web.Models;

/// <summary>
/// Mirrors the API's WhatsAppWebhookStatusDto. Keep the nullability in step with it: a mismatch
/// makes System.Text.Json throw and the console reports the delivery path as unknown.
/// </summary>
public class WhatsAppWebhookStatusModel
{
    public bool Registered { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string ExpectedUrl { get; set; } = string.Empty;
    public string? WebhookId { get; set; }
    public List<string> Events { get; set; } = [];
    public string Action { get; set; } = "unchanged";
    public string? Message { get; set; }
    public DateTime? LastTriggeredAtUtc { get; set; }
}
