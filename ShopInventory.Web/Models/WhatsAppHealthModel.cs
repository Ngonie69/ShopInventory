namespace ShopInventory.Web.Models;

public class WhatsAppHealthModel
{
    public string BaseUrl { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string Status { get; set; } = "unknown";
    public string? Version { get; set; }
    public double? UptimeSeconds { get; set; }
    public DateTime? ReportedAtUtc { get; set; }
    public DateTime CheckedAtUtc { get; set; }
    public int? TotalSessions { get; set; }
    public int? ConnectedSessions { get; set; }
    public int? DisconnectedSessions { get; set; }
}