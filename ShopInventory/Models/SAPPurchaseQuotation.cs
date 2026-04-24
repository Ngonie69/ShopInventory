using System.Text.Json.Serialization;

namespace ShopInventory.Models;

public class SAPPurchaseQuotation
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

    [JsonPropertyName("Comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("DocTotal")]
    public decimal? DocTotal { get; set; }

    [JsonPropertyName("VatSum")]
    public decimal? VatSum { get; set; }

    [JsonPropertyName("DiscountPercent")]
    public decimal? DiscountPercent { get; set; }

    [JsonPropertyName("TotalDiscount")]
    public decimal? TotalDiscount { get; set; }

    [JsonPropertyName("DocCurrency")]
    public string? DocCurrency { get; set; }

    [JsonPropertyName("DocumentStatus")]
    public string? DocumentStatus { get; set; }

    [JsonPropertyName("Cancelled")]
    public string? Cancelled { get; set; }

    [JsonPropertyName("Address")]
    public string? Address { get; set; }

    [JsonPropertyName("Address2")]
    public string? Address2 { get; set; }

    [JsonPropertyName("DocumentLines")]
    public List<SAPPurchaseQuotationLine>? DocumentLines { get; set; }
}

public class SAPPurchaseQuotationLine
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

    [JsonPropertyName("LineTotal")]
    public decimal? LineTotal { get; set; }

    [JsonPropertyName("WarehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("DiscountPercent")]
    public decimal? DiscountPercent { get; set; }

    [JsonPropertyName("TaxCode")]
    public string? TaxCode { get; set; }

    [JsonPropertyName("UoMCode")]
    public string? UoMCode { get; set; }

    [JsonPropertyName("UoMEntry")]
    public int? UoMEntry { get; set; }
}