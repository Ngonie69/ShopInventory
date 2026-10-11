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

    /// <summary>
    /// An administrator stopped all sending. False with no session means none could be chosen yet: the
    /// gateway's ready one is taken as soon as there is one.
    /// </summary>
    public bool SendingStopped { get; set; }

    /// <summary>
    /// With no session saved and sending not stopped: the ready session the next pass will take, or
    /// null when there is none it can take.
    /// </summary>
    public string? AutomaticSessionName { get; set; }

    /// <summary>
    /// With no session saved: several are ready and none carries the documents name, so a person has
    /// to choose. The page cannot work this out itself; it does not know the name.
    /// </summary>
    public bool SeveralSessionsReady { get; set; }

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

    /// <summary>
    /// When the scan for new SAP invoices last ran, the producer of automatic sends; null before its
    /// first pass, or where it is not scheduled (SAP switched off).
    /// </summary>
    public DateTime? InvoiceScanAtUtc { get; set; }

    /// <summary>The last SAP invoice DocEntry the scan has read.</summary>
    public int? InvoiceScanLastDocEntry { get; set; }

    public string? SettingsChangedBy { get; set; }

    public DateTime? SettingsChangedAtUtc { get; set; }
}
