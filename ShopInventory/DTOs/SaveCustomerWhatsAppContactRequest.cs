namespace ShopInventory.DTOs;

/// <summary>Save a WhatsApp number a customer gave for their documents.</summary>
public sealed class SaveCustomerWhatsAppContactRequest
{
    /// <summary>The account customer's SAP card. Give this or <see cref="RouteCustomerId"/>, not both.</summary>
    public string? CardCode { get; set; }

    /// <summary>The van route customer.</summary>
    public int? RouteCustomerId { get; set; }

    /// <summary>The number as written: 0771234567, +263 77 123 4567 and 263771234567 are all accepted.</summary>
    public string Phone { get; set; } = string.Empty;

    /// <summary>Who answers on it.</summary>
    public string? ContactName { get; set; }

    /// <summary>Whether new invoices are sent here without anyone pressing Send.</summary>
    public bool AutoSendInvoices { get; set; } = true;

    /// <summary>The person saving it confirms the customer agreed to receive their documents here.</summary>
    public bool ConsentConfirmed { get; set; }

    /// <summary>How the customer agreed, in the saver's words.</summary>
    public string? ConsentNote { get; set; }

    /// <summary>
    /// The same shop's cards in its other currencies, to save the number on too. SAP keeps one card
    /// per currency, and a shop usually wants every one of its invoices.
    /// </summary>
    public List<string>? AlsoApplyToCardCodes { get; set; }

    /// <summary>The customer's name, used only when SAP cannot be asked for it.</summary>
    public string? OwnerName { get; set; }
}
