namespace ShopInventory.DTOs;

/// <summary>
/// A WhatsApp number on a customer, with the consent it was saved under.
/// </summary>
/// <remarks>
/// Sources and statuses travel as their names, not as numbers, so the web's copy of this shape cannot
/// silently read one value as another if either side's list is ever reordered.
/// </remarks>
public sealed class CustomerWhatsAppContactDto
{
    public int Id { get; set; }

    public string? CardCode { get; set; }

    public int? RouteCustomerId { get; set; }

    public string OwnerName { get; set; } = string.Empty;

    /// <summary>The whole number. Shown only to those who keep the register.</summary>
    public string PhoneE164 { get; set; } = string.Empty;

    public string PhoneMasked { get; set; } = string.Empty;

    public string? ContactName { get; set; }

    public bool AutoSendInvoices { get; set; }

    /// <summary>Web, VanHandset or Till.</summary>
    public string ConsentSource { get; set; } = string.Empty;

    public string? ConsentNote { get; set; }

    public DateTime ConsentRecordedAtUtc { get; set; }

    public string ConsentRecordedBy { get; set; } = string.Empty;

    public bool IsOptedOut { get; set; }

    public DateTime? OptedOutAtUtc { get; set; }

    /// <summary>Web or StopKeyword.</summary>
    public string? OptedOutSource { get; set; }

    public string? OptedOutBy { get; set; }

    /// <summary>What the last WhatsApp check said; null when it was never asked.</summary>
    public bool? WhatsAppExists { get; set; }

    public DateTime? WhatsAppCheckedAtUtc { get; set; }

    public bool IsRemoved { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
