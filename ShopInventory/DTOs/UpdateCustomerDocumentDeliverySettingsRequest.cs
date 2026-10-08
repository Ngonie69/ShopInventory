namespace ShopInventory.DTOs;

/// <summary>The run-time switches for sending customer documents.</summary>
public sealed class UpdateCustomerDocumentDeliverySettingsRequest
{
    /// <summary>Whether invoices go to opted-in customers without anyone pressing Send.</summary>
    public bool AutoSendEnabled { get; set; }

    /// <summary>The OpenWA session to send from; blank stops all sending.</summary>
    public string? WhatsAppSessionId { get; set; }

    /// <summary>The most automatic sends in a CAT day.</summary>
    public int MaxAutoPerDay { get; set; }
}
