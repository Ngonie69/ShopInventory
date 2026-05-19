using System.Text.Json.Serialization;

namespace ShopInventory.Web.Models;

public class WhatsAppSessionModel
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? Phone { get; set; }

    public string? PushName { get; set; }

    public DateTime? ConnectedAt { get; set; }

    [JsonPropertyName("lastActive")]
    public DateTime? LastActive { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}