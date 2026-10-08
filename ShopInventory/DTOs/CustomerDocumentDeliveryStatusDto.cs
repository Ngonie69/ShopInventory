namespace ShopInventory.DTOs;

/// <summary>
/// Where sending customer documents stands: the switches, today's counts against the caps, and what is
/// waiting or needs a person.
/// </summary>
public sealed class CustomerDocumentDeliveryStatusDto
{
    /// <summary>Whether the delivery job runs at all on this installation.</summary>
    public bool Enabled { get; set; }

    /// <summary>Whether the node that answered has the gateway configured.</summary>
    public bool GatewayConfigured { get; set; }

    public bool AutoSendEnabled { get; set; }

    public string? SessionId { get; set; }

    public string? SessionName { get; set; }

    /// <summary>The session's state as the gateway reports it; null when it could not be asked.</summary>
    public string? SessionStatus { get; set; }

    public string? SessionPhone { get; set; }

    public string? SessionError { get; set; }

    public List<WhatsAppSessionOptionDto> Sessions { get; set; } = [];

    public int SentToday { get; set; }

    public int SentLastHour { get; set; }

    public int AutoSentToday { get; set; }

    public int MaxAutoPerDay { get; set; }

    public int MaxPerHour { get; set; }

    public int HardMaxPerDay { get; set; }

    public string AutoWindow { get; set; } = string.Empty;

    public bool WithinAutoWindow { get; set; }

    /// <summary>Pending or preparing.</summary>
    public int Waiting { get; set; }

    public int WaitingForFiscal { get; set; }

    public int Held { get; set; }

    public int Uncertain { get; set; }

    public int FailedToday { get; set; }

    public DateTime? LastSentAtUtc { get; set; }

    public string? SettingsChangedBy { get; set; }

    public DateTime? SettingsChangedAtUtc { get; set; }
}
