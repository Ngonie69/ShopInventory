namespace ShopInventory.DTOs;

/// <summary>Send an invoice to one or more WhatsApp numbers.</summary>
public sealed class RequestInvoiceWhatsAppRequest
{
    /// <summary>Numbers already saved on the invoice's customer.</summary>
    public List<int>? ContactIds { get; set; }

    /// <summary>A number typed for this send, when the one wanted is not saved.</summary>
    public string? OneOffPhone { get; set; }

    /// <summary>Who answers on the one-off number.</summary>
    public string? OneOffName { get; set; }

    /// <summary>The sender confirms the customer asked for the invoice on the one-off number.</summary>
    public bool ConsentAffirmed { get; set; }

    /// <summary>Also save the one-off number on the customer, with that consent.</summary>
    public bool SaveAsContact { get; set; }

    /// <summary>When saving it, whether the customer's future invoices go there on their own.</summary>
    public bool AutoSendFutureInvoices { get; set; }
}
