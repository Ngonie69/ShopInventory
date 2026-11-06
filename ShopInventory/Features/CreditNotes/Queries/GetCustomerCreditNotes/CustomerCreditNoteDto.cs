namespace ShopInventory.Features.CreditNotes.Queries.GetCustomerCreditNotes;

/// <summary>
/// One credit note as its customer is shown it. Every amount is in <see cref="Currency"/>.
/// </summary>
/// <remarks>
/// Mirrored by hand in <c>ShopInventory.Web/Models/CustomerCreditNoteModels.cs</c>: change both together,
/// nullability included.
/// </remarks>
public sealed class CustomerCreditNoteDto
{
    public int DocEntry { get; init; }

    public int DocNum { get; init; }

    public DateTime DocDate { get; init; }

    public string CardCode { get; init; } = string.Empty;

    public string? CardName { get; init; }

    public string Currency { get; init; } = "USD";

    /// <summary>The amount credited, VAT included.</summary>
    public decimal DocTotal { get; init; }

    /// <summary>The VAT included in <see cref="DocTotal"/>.</summary>
    public decimal VatTotal { get; init; }

    /// <summary>How much has been set against invoices or refunded.</summary>
    public decimal AppliedAmount { get; init; }

    /// <summary>What is still to be used: <see cref="DocTotal"/> less <see cref="AppliedAmount"/>.</summary>
    public decimal CreditLeft { get; init; }

    /// <summary>The number of the invoice the note was raised against; 0 when it was not raised against one.</summary>
    public int CreditedInvoiceDocNum { get; init; }

    /// <summary>The reasons on the note's lines, from SAP's own list; null when none was given.</summary>
    public string? Reason { get; init; }

    /// <summary>Whether ZIMRA holds a receipt for the note on this customer's account.</summary>
    public bool IsFiscalised { get; init; }

    /// <summary>The receipt's number on its device; 0 when there is no receipt.</summary>
    public int FiscalReceiptNumber { get; init; }

    /// <summary>The verification code printed beside the receipt's QR, grouped in fours.</summary>
    public string? FiscalVerificationCode { get; init; }

    /// <summary>The address ZIMRA verifies the receipt at — the receipt's QR payload.</summary>
    public string? FiscalVerificationUrl { get; init; }

    public List<CustomerCreditNoteLineDto> Lines { get; init; } = [];
}
