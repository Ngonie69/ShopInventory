namespace ShopInventory.Features.CreditNotes.Queries.GetCustomerCreditNotes;

/// <summary>One credited line, VAT included, in its note's currency.</summary>
public sealed class CustomerCreditNoteLineDto
{
    public string? ItemCode { get; init; }

    public string? Description { get; init; }

    public decimal Quantity { get; init; }

    public string? UoMCode { get; init; }

    /// <summary>The price per unit credited, after the line discount and with VAT.</summary>
    public decimal UnitPrice { get; init; }

    /// <summary>The line's total credited, after the line discount and with VAT.</summary>
    public decimal LineTotal { get; init; }

    /// <summary>The reason given on this line, from SAP's own list.</summary>
    public string? Reason { get; init; }
}
