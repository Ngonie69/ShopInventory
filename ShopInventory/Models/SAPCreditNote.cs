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
    public decimal DiscountPercent { get; set; }

    [JsonPropertyName("TotalDiscount")]
    public decimal TotalDiscount { get; set; }

    [JsonPropertyName("Address")]
    public string? Address { get; set; }

    [JsonPropertyName("Address2")]
    public string? Address2 { get; set; }

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

    [JsonPropertyName("WarehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("TaxCode")]
    public string? TaxCode { get; set; }

    [JsonPropertyName("DiscountPercent")]
    public decimal DiscountPercent { get; set; }

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
}
