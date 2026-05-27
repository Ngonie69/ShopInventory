using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesEnvelope<T>
{
    [JsonPropertyName("success")]
    public T? Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}