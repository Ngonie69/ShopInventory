namespace ShopInventory.Web.Models;

// Hand-written mirrors of the API's ShopInventory/Features/CreditNotes/Queries/GetCustomerCreditNotes
// types. Nothing checks that the two agree: a value type the API makes nullable and this side does not
// throws on read and loses the whole list, and a property missing here is silently dropped. Change both
// together.

/// <summary>A customer's credit notes over a period, newest first.</summary>
public class CustomerCreditNotesResponse
{
    public List<CustomerCreditNote> CreditNotes { get; set; } = new();
}

/// <summary>One credit note as its customer is shown it. Every amount is in <see cref="Currency"/>.</summary>
public class CustomerCreditNote
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public DateTime DocDate { get; set; }
    public string CardCode { get; set; } = string.Empty;
    public string? CardName { get; set; }
    public string Currency { get; set; } = "USD";

    /// <summary>The amount credited, VAT included.</summary>
    public decimal DocTotal { get; set; }

    /// <summary>The VAT included in <see cref="DocTotal"/>.</summary>
    public decimal VatTotal { get; set; }

    /// <summary>How much has been set against invoices or refunded.</summary>
    public decimal AppliedAmount { get; set; }

    /// <summary>What is still to be used.</summary>
    public decimal CreditLeft { get; set; }

    /// <summary>The invoice the note was raised against; 0 when it was raised against none.</summary>
    public int CreditedInvoiceDocNum { get; set; }

    /// <summary>The reasons on its lines, from SAP's own list; null when none was given.</summary>
    public string? Reason { get; set; }

    public bool IsFiscalised { get; set; }

    /// <summary>The fiscal receipt's number; 0 when there is no receipt.</summary>
    public int FiscalReceiptNumber { get; set; }

    public string? FiscalVerificationCode { get; set; }

    /// <summary>The address ZIMRA verifies the receipt at.</summary>
    public string? FiscalVerificationUrl { get; set; }

    public List<CustomerCreditNoteLine> Lines { get; set; } = new();
}

/// <summary>One credited line, VAT included, in its note's currency.</summary>
public class CustomerCreditNoteLine
{
    public string? ItemCode { get; set; }
    public string? Description { get; set; }
    public decimal Quantity { get; set; }
    public string? UoMCode { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public string? Reason { get; set; }
}
