using System.Text.Json.Serialization;

namespace ShopInventory.Models;

public class SAPPurchaseRequest
{
    [JsonPropertyName("DocEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("DocNum")]
    public int DocNum { get; set; }

    [JsonPropertyName("DocDate")]
    public string? DocDate { get; set; }

    [JsonPropertyName("RequriedDate")]
    public string? RequriedDate { get; set; }

    [JsonPropertyName("Comments")]
    public string? Comments { get; set; }

    /// <summary>
    /// The requester's user code, e.g. "Wkshop2".
    /// </summary>
    /// <remarks>
    /// A string, both in the Service Layer metadata (<c>Edm.String</c>) and in what SAP returns.
    /// This was declared <c>int?</c>, so deserializing any purchase request that had a requester
    /// threw — which is every real one. Nothing exercised this path until the integration tests.
    /// </remarks>
    [JsonPropertyName("Requester")]
    public string? Requester { get; set; }

    [JsonPropertyName("RequesterName")]
    public string? RequesterName { get; set; }

    [JsonPropertyName("DocumentStatus")]
    public string? DocumentStatus { get; set; }

    [JsonPropertyName("Cancelled")]
    public string? Cancelled { get; set; }

    [JsonPropertyName("DocTotal")]
    public decimal? DocTotal { get; set; }

    [JsonPropertyName("DocumentLines")]
    public List<SAPPurchaseRequestLine>? DocumentLines { get; set; }
}

public class SAPPurchaseRequestLine
{
    [JsonPropertyName("LineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("ItemCode")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("ItemDescription")]
    public string? ItemDescription { get; set; }

    [JsonPropertyName("Quantity")]
    public decimal? Quantity { get; set; }

    [JsonPropertyName("OpenQuantity")]
    public decimal? OpenQuantity { get; set; }

    [JsonPropertyName("WarehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("LineVendor")]
    public string? LineVendor { get; set; }

    [JsonPropertyName("RequiredDate")]
    public string? RequiredDate { get; set; }

    [JsonPropertyName("UoMCode")]
    public string? UoMCode { get; set; }

    [JsonPropertyName("UoMEntry")]
    public int? UoMEntry { get; set; }
}