using System.Text.Json.Serialization;

namespace ShopInventory.Models.Revmax;

/// <summary>
/// Structured REVMax currency payload under the legacy CurrenciesXml field name.
/// </summary>
public sealed class RevmaxRequestCurrency
{
    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Amount")]
    public string? Amount { get; set; }

    [JsonPropertyName("Rate")]
    public string? Rate { get; set; }
}