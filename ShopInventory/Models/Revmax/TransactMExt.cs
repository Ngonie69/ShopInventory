using System.Text.Json.Serialization;

namespace ShopInventory.Models.Revmax;

/// <summary>
/// Request DTO for TransactMExt endpoint.
/// Extends TransactMRequest with additional fiscal reference fields.
/// </summary>
public class TransactMExtRequest : TransactMRequest
{
    /// <summary>
    /// Reference device ID.
    /// </summary>
    [JsonPropertyOrder(1)]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public int? refDeviceId { get; set; }

    /// <summary>
    /// Reference receipt global number from original invoice.
    /// </summary>
    [JsonPropertyOrder(2)]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public long? refReceiptGlobalNo { get; set; }

    /// <summary>
    /// Reference fiscal day number from original invoice.
    /// </summary>
    [JsonPropertyOrder(3)]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public int? refFiscalDayNo { get; set; }
}

/// <summary>
/// Response DTO from TransactMExt endpoint.
/// </summary>
public class TransactMExtResponse : TransactMResponse
{
    // Same structure as TransactMResponse
}
