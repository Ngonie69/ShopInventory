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

    /// <summary>A line name the device will accept, from the first candidate that carries one.</summary>
    /// <remarks>
    /// REVMax validates ITEMNAME1 and ITEMNAME2 separately and refuses the whole transaction when
    /// either is blank: a credit note filed with ITEMNAME2 set to "" came back as "Invalid ITEMNAME2..
    /// Item Name cannot be empty. An empty value was supplied. Verify if Item Names are supplied for
    /// all line items", with nothing filed. Both names carry the same description, so the caller
    /// passes what it has - the description first, then the code or line number - and a line that has
    /// none of them still files a name rather than failing the document.
    /// </remarks>
    public static string Name(params string?[] candidates) =>
        Array.Find(candidates, c => !string.IsNullOrWhiteSpace(c)) ?? "Item";
}