namespace ShopInventory.Models.Entities;

/// <summary>Where a customer's agreement to receive documents on WhatsApp was recorded.</summary>
public enum WhatsAppConsentSource
{
    /// <summary>A member of staff saved the number in the web app.</summary>
    Web = 0,

    /// <summary>A van rep saved it on the handset at the shop.</summary>
    VanHandset = 1,

    /// <summary>A cashier took it at the till for one sale.</summary>
    Till = 2
}
