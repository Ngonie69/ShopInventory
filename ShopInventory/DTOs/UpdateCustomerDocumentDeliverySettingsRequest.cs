namespace ShopInventory.DTOs;

/// <summary>The run-time switches for sending customer documents.</summary>
public sealed class UpdateCustomerDocumentDeliverySettingsRequest
{
    /// <summary>Whether invoices go to opted-in customers without anyone pressing Send.</summary>
    public bool AutoSendEnabled { get; set; }

    /// <summary>
    /// The OpenWA session to send from. Blank leaves the choice to the system: the gateway's ready
    /// session is used, the one named for documents when there are several.
    /// </summary>
    public string? WhatsAppSessionId { get; set; }

    /// <summary>
    /// Stops all sending: no session is used and none is chosen automatically until the settings are
    /// saved again without it. Ignored when <see cref="WhatsAppSessionId"/> names a session.
    /// </summary>
    public bool StopSending { get; set; }

    /// <summary>The most automatic sends in a CAT day.</summary>
    public int MaxAutoPerDay { get; set; }
}
