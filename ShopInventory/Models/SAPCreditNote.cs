using System.Text.Json.Serialization;

namespace ShopInventory.Models;

/// <summary>
/// SAP Business One Credit Memo (A/R Credit Memo) model
/// </summary>
public class SAPCreditNote
{
    [JsonPropertyName("DocEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("DocNum")]
    public int DocNum { get; set; }

    [JsonPropertyName("DocDate")]
    public string? DocDate { get; set; }

    [JsonPropertyName("DocDueDate")]
    public string? DocDueDate { get; set; }

    [JsonPropertyName("UpdateDate")]
    public string? UpdateDate { get; set; }

    /// <summary>
    /// The time of day of UpdateDate, as "HH:mm:ss". Only the projection sweep's version poll asks
    /// for it; every other credit-note read leaves it null.
    /// </summary>
    [JsonPropertyName("UpdateTime")]
    public string? UpdateTime { get; set; }

    [JsonPropertyName("CardCode")]
    public string? CardCode { get; set; }

    [JsonPropertyName("CardName")]
    public string? CardName { get; set; }

    [JsonPropertyName("NumAtCard")]
    public string? NumAtCard { get; set; } // Customer reference number

    [JsonPropertyName("Comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("DocTotal")]
    public decimal DocTotal { get; set; }

    [JsonPropertyName("DocTotalFc")]
    public decimal DocTotalFc { get; set; }

    [JsonPropertyName("VatSum")]
    public decimal VatSum { get; set; }

    [JsonPropertyName("DocCurrency")]
    public string? DocCurrency { get; set; }

    [JsonPropertyName("SalesPersonCode")]
    public int? SalesPersonCode { get; set; }

    [JsonPropertyName("DocumentStatus")]
    public string? DocumentStatus { get; set; } // bost_Open, bost_Close

    [JsonPropertyName("Cancelled")]
    public string? Cancelled { get; set; } // tYES, tNO

    [JsonPropertyName("DiscountPercent")]
    public decimal? DiscountPercent { get; set; }

    [JsonPropertyName("TotalDiscount")]
    public decimal? TotalDiscount { get; set; }

    [JsonPropertyName("Address")]
    public string? Address { get; set; }

    [JsonPropertyName("Address2")]
    public string? Address2 { get; set; }

    /// <summary>Key of the Attachments2 record holding the document's attachments, when it has any.</summary>
    [JsonPropertyName("AttachmentEntry")]
    public int? AttachmentEntry { get; set; }

    /// <summary>
    /// The approval-procedure state: dasWithout, dasPending, dasApproved, dasRejected, dasGenerated,
    /// dasGeneratedbyAuthorizer or dasCancelled. Read on drafts; never selected on posted credit notes.
    /// </summary>
    [JsonPropertyName("AuthorizationStatus")]
    public string? AuthorizationStatus { get; set; }

    /// <summary>On a row read from Drafts, which document the draft is for — oCreditNotes for an A/R credit memo.</summary>
    [JsonPropertyName("DocObjectCode")]
    public string? DocObjectCode { get; set; }

    // Reference to original invoice (if credit note was created against an invoice)
    [JsonPropertyName("BaseEntry")]
    public int? BaseEntry { get; set; }

    [JsonPropertyName("BaseType")]
    public int? BaseType { get; set; } // 13 = A/R Invoice

    [JsonPropertyName("DocumentLines")]
    public List<SAPCreditNoteLine>? DocumentLines { get; set; }
}

/// <summary>
/// SAP Business One Credit Memo Line model
/// </summary>
public class SAPCreditNoteLine
{
    [JsonPropertyName("LineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("ItemCode")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("ItemDescription")]
    public string? ItemDescription { get; set; }

    [JsonPropertyName("Quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("UnitPrice")]
    public decimal UnitPrice { get; set; }

    [JsonPropertyName("Price")]
    public decimal Price { get; set; }

    [JsonPropertyName("LineTotal")]
    public decimal LineTotal { get; set; }

    /// <summary>
    /// Not the line's VAT on anything read from the Service Layer. Read <see cref="Vat"/>.
    /// </summary>
    /// <remarks>
    /// <c>VatSum</c> is a header property; a document line read from the Service Layer comes back
    /// without one, so this stays 0. Kept because the SQL reads over <c>RIN1</c> do fill it.
    /// </remarks>
    [JsonPropertyName("VatSum")]
    public decimal VatSum { get; set; }

    /// <summary>
    /// The VAT on this line: what <see cref="GrossTotal"/> holds beyond <see cref="LineTotal"/>.
    /// </summary>
    /// <remarks>
    /// The projection stored <see cref="VatSum"/> for this, which is 0 from the Service Layer, so every
    /// reader that added a memo line's VAT to its net was adding nothing. On 2026-10-12 memo 60678 gave
    /// back the whole of a 0.87 van invoice and the handset was told 0.75 — "0.87 sold, 0.75 credited
    /// back" on its dashboard — and a POD report could not see such an invoice as fully credited.
    /// A line with no gross total falls back to <see cref="VatSum"/>, which is what a SQL read fills.
    /// </remarks>
    public decimal Vat() => GrossTotal != 0m ? GrossTotal - LineTotal : VatSum;

    [JsonPropertyName("WarehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("TaxCode")]
    public string? TaxCode { get; set; }

    /// <summary>SAP VAT group code. Where the line's tax actually lives; <see cref="TaxCode"/> comes back null.</summary>
    [JsonPropertyName("VatGroup")]
    public string? VatGroup { get; set; }

    /// <summary>Gross unit price BEFORE the line discount.</summary>
    [JsonPropertyName("GrossPrice")]
    public decimal GrossPrice { get; set; }

    /// <summary>Gross unit price after the line discount — what the customer paid, and what a receipt declares.</summary>
    [JsonPropertyName("PriceAfterVAT")]
    public decimal PriceAfterVAT { get; set; }

    /// <summary>Gross line total after the line discount.</summary>
    [JsonPropertyName("GrossTotal")]
    public decimal GrossTotal { get; set; }

    [JsonPropertyName("DiscountPercent")]
    public decimal? DiscountPercent { get; set; }

    [JsonPropertyName("UoMCode")]
    public string? UoMCode { get; set; }

    [JsonPropertyName("UoMEntry")]
    public int? UoMEntry { get; set; }

    [JsonPropertyName("LineStatus")]
    public string? LineStatus { get; set; } // bost_Open, bost_Close

    // Reference to original invoice line
    [JsonPropertyName("BaseEntry")]
    public int? BaseEntry { get; set; }

    [JsonPropertyName("BaseLine")]
    public int? BaseLine { get; set; }

    [JsonPropertyName("BaseType")]
    public int? BaseType { get; set; } // 13 = A/R Invoice

    [JsonPropertyName("U_Reasons")]
    public string? CreditReason { get; set; }


    /// <summary>
    /// SAP's gross profit on the line, in the company's local currency (USD): the line total less the
    /// stock cost SAP relieved when the goods left. Null when SAP did not send it.
    /// </summary>
    [JsonPropertyName("GrossProfit")]
    public decimal? GrossProfit { get; set; }
}
