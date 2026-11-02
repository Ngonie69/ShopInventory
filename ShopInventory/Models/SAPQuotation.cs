using System.Text.Json.Serialization;

namespace ShopInventory.Models;

/// <summary>
/// SAP Business One Sales Quotation model
/// </summary>
public class SAPQuotation : ISapDocumentAmounts
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
    public string? NumAtCard { get; set; }

    [JsonPropertyName("ContactPersonCode")]
    public int? ContactPersonCode { get; set; }

    [JsonPropertyName("Comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("DocTotal")]
    public decimal? DocTotal { get; set; }

    [JsonPropertyName("DocTotalFc")]
    public decimal? DocTotalFc { get; set; }

    [JsonPropertyName("VatSum")]
    public decimal? VatSum { get; set; }

    /// <summary>The tax in the document's own currency. VatSum is in local currency (USD).</summary>
    [JsonPropertyName("VatSumFc")]
    public decimal? VatSumFc { get; set; }

    /// <summary>The rate SAP converted the document at: document currency per unit of local currency.</summary>
    [JsonPropertyName("DocRate")]
    public decimal? DocRate { get; set; }

    [JsonPropertyName("DocCurrency")]
    public string? DocCurrency { get; set; }

    [JsonPropertyName("U_OrderNumber")]
    public string? U_OrderNumber { get; set; }

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

    /// <summary>The header discount in the document's own currency.</summary>
    [JsonPropertyName("TotalDiscountFC")]
    public decimal? TotalDiscountFC { get; set; }

    [JsonPropertyName("Address")]
    public string? Address { get; set; }

    [JsonPropertyName("Address2")]
    public string? Address2 { get; set; }

    [JsonPropertyName("ShipToCode")]
    public string? ShipToCode { get; set; }

    [JsonPropertyName("PayToCode")]
    public string? PayToCode { get; set; }

    [JsonPropertyName("DocumentLines")]
    public List<SAPQuotationLine>? DocumentLines { get; set; }
}

/// <summary>
/// SAP Business One Sales Quotation Line model
/// </summary>
public class SAPQuotationLine : ISapDocumentLineAmounts
{
    [JsonPropertyName("LineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("ItemCode")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("ItemDescription")]
    public string? ItemDescription { get; set; }

    [JsonPropertyName("Quantity")]
    public decimal? Quantity { get; set; }

    [JsonPropertyName("UnitPrice")]
    public decimal? UnitPrice { get; set; }

    [JsonPropertyName("Price")]
    public decimal? Price { get; set; }

    [JsonPropertyName("LineTotal")]
    public decimal? LineTotal { get; set; }

    /// <summary>The line total in the document's own currency; zero on a local-currency document.</summary>
    [JsonPropertyName("RowTotalFC")]
    public decimal? RowTotalFC { get; set; }

    [JsonPropertyName("WarehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("TaxCode")]
    public string? TaxCode { get; set; }

    [JsonPropertyName("DiscountPercent")]
    public decimal? DiscountPercent { get; set; }

    [JsonPropertyName("UoMCode")]
    public string? UoMCode { get; set; }
}
