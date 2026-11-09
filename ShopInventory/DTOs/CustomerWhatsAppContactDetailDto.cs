namespace ShopInventory.DTOs;

/// <summary>
/// One saved WhatsApp number in full: who first added it, what has been sent to it, and everything
/// that has happened to it since.
/// </summary>
public sealed class CustomerWhatsAppContactDetailDto
{
    public CustomerWhatsAppContactDto Contact { get; set; } = new();

    public string? RouteCustomerCode { get; set; }

    /// <summary>
    /// Who first saved the number on this customer. Not <see cref="CustomerWhatsAppContactDto.ConsentRecordedBy"/>,
    /// which a later save with fresh consent overwrites. Null when nothing recorded it.
    /// </summary>
    public string? AddedBy { get; set; }

    public DateTime AddedAtUtc { get; set; }

    /// <summary>Web, VanHandset or Till; null when the first save's channel was not kept.</summary>
    public string? AddedVia { get; set; }

    public int SentCount { get; set; }

    public int WaitingCount { get; set; }

    public DateTime? LastSentAtUtc { get; set; }

    /// <summary>The latest documents sent, or waiting to be sent, to this number on this customer.</summary>
    public List<CustomerDocumentDeliveryDto> RecentDeliveries { get; set; } = [];

    /// <summary>Newest first.</summary>
    public List<CustomerWhatsAppContactEventDto> History { get; set; } = [];
}

/// <summary>One thing that happened to a saved number.</summary>
public sealed class CustomerWhatsAppContactEventDto
{
    public DateTime AtUtc { get; set; }

    /// <summary>Saved, Changed, Checked, OptedOut or Removed.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>What happened, as a sentence.</summary>
    public string What { get; set; } = string.Empty;

    /// <summary>The person, or null when nothing recorded who.</summary>
    public string? Who { get; set; }

    /// <summary>Web, VanHandset, Till or StopKeyword, when known.</summary>
    public string? Via { get; set; }
}
