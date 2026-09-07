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

    /// <summary>
    /// Why the gateway could not be reached. Set by the Web only; the API never sends it, and a
    /// healthy gateway leaves it null.
    /// </summary>
    /// <remarks>
    /// This exists because the reason used to be written into <see cref="BaseUrl"/>, which put an
    /// error sentence under a "Host" label on the console and left the operator with no idea what
    /// the host actually was.
    /// </remarks>
    public string? Message { get; set; }
}