namespace ShopInventory.DTOs;

/// <summary>
/// Change who a saved number belongs to and whether it receives invoices on its own. The number itself
/// cannot change here: a different number is a different consent, saved as a new contact.
/// </summary>
public sealed class UpdateCustomerWhatsAppContactRequest
{
    public string? ContactName { get; set; }

    public bool AutoSendInvoices { get; set; }
}
