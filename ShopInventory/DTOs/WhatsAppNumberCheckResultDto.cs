namespace ShopInventory.DTOs;

/// <summary>What is known about a number before a document is sent to it.</summary>
public sealed class WhatsAppNumberCheckResultDto
{
    public string Input { get; set; } = string.Empty;

    public bool IsValid { get; set; }

    /// <summary>The number in E.164, when it is one.</summary>
    public string? PhoneE164 { get; set; }

    public string? PhoneMasked { get; set; }

    /// <summary>The customer asked not to receive documents on this number.</summary>
    public bool OptedOut { get; set; }

    /// <summary>The contact this number is already saved as on the customer, if it is.</summary>
    public int? ExistingContactId { get; set; }

    public string? Message { get; set; }
}
