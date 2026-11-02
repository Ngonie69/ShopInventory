using System.Text.Json.Serialization;

namespace ShopInventory.Models;

public class SAPGoodsReceiptPurchaseOrder : ISapDocumentAmounts
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

    /// <summary>The total in the document's own currency; zero on a local-currency (USD) document.</summary>
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

    [JsonPropertyName("DiscountPercent")]
    public decimal? DiscountPercent { get; set; }

    [JsonPropertyName("TotalDiscount")]
    public decimal? TotalDiscount { get; set; }

    /// <summary>The header discount in the document's own currency.</summary>
    [JsonPropertyName("TotalDiscountFC")]
    public decimal? TotalDiscountFC { get; set; }

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
    public List<SAPGoodsReceiptPurchaseOrderLine>? DocumentLines { get; set; }
}

public class SAPGoodsReceiptPurchaseOrderLine : ISapDocumentLineAmounts
{
    [JsonPropertyName("LineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("ItemCode")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("ItemDescription")]
    public string? ItemDescription { get; set; }

    [JsonPropertyName("Quantity")]
    public decimal? Quantity { get; set; }

    [JsonPropertyName("LineTotal")]
    public decimal? LineTotal { get; set; }

    /// <summary>The line total in the document's own currency; zero on a local-currency document.</summary>
    [JsonPropertyName("RowTotalFC")]
    public decimal? RowTotalFC { get; set; }

    [JsonPropertyName("WarehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("TaxCode")]
    public string? TaxCode { get; set; }

    [JsonPropertyName("UoMCode")]
    public string? UoMCode { get; set; }

    [JsonPropertyName("UoMEntry")]
    public int? UoMEntry { get; set; }

    [JsonPropertyName("BaseEntry")]
    public int? BaseEntry { get; set; }

    [JsonPropertyName("BaseLine")]
    public int? BaseLine { get; set; }

    [JsonPropertyName("BaseType")]
    public int? BaseType { get; set; }
}