using System.Text.Json.Serialization;

namespace ShopInventory.Models.Revmax;

/// <summary>
/// Structured REVMax line item payload under the legacy ItemsXml field name.
/// </summary>
public sealed class RevmaxRequestItem
{
    [JsonPropertyName("HH")]
    public string? HH { get; set; }

    [JsonPropertyName("ITEMCODE")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("ITEMNAME1")]
    public string? ItemName1 { get; set; }

    [JsonPropertyName("ITEMNAME2")]
    public string? ItemName2 { get; set; }

    [JsonPropertyName("QTY")]
    public string? Qty { get; set; }

    [JsonPropertyName("PRICE")]
    public string? Price { get; set; }

    [JsonPropertyName("AMT")]
    public string? Amt { get; set; }

    [JsonPropertyName("TAX")]
    public string? Tax { get; set; }

    [JsonPropertyName("TAXR")]
    public string? TaxR { get; set; }
}