namespace ShopInventory.DTOs;

/// <summary>
/// One page of every WhatsApp number saved on a customer, with the counts the page's filters show.
/// </summary>
public sealed class CustomerWhatsAppContactRegisterDto
{
    public List<CustomerWhatsAppContactRegisterItemDto> Items { get; set; } = [];

    /// <summary>Rows matching the filters, across every page.</summary>
    public int TotalCount { get; set; }

    public int Page { get; set; }

    public int PageSize { get; set; }

    /// <summary>The whole register, whatever the filters, so the counts never move as you filter.</summary>
    public CustomerWhatsAppContactRegisterSummaryDto Summary { get; set; } = new();
}

/// <summary>A saved number, its owner's code, and what has been sent to it.</summary>
public sealed class CustomerWhatsAppContactRegisterItemDto
{
    public CustomerWhatsAppContactDto Contact { get; set; } = new();

    /// <summary>The route customer's own code, for a number saved on a van route customer.</summary>
    public string? RouteCustomerCode { get; set; }

    /// <summary>Documents that reached this number.</summary>
    public int SentCount { get; set; }

    public DateTime? LastSentAtUtc { get; set; }

    /// <summary>Documents queued or waiting for a fiscal receipt for this number right now.</summary>
    public int WaitingCount { get; set; }
}

/// <summary>How the numbers still on a customer break down. Removed ones are counted on their own.</summary>
public sealed class CustomerWhatsAppContactRegisterSummaryDto
{
    /// <summary>Numbers on a customer — automatic, on request and opted out.</summary>
    public int Linked { get; set; }

    public int Automatic { get; set; }

    public int OnRequest { get; set; }

    public int OptedOut { get; set; }

    /// <summary>The last WhatsApp check said the number has no account.</summary>
    public int NotOnWhatsApp { get; set; }

    public int Removed { get; set; }
}
