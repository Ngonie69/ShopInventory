using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

/// <summary>
/// What a van is carrying, as its own handset counts it.
/// </summary>
/// <remarks>
/// <para>This exists because nothing else knows. A van's stock is decremented by sales that are signed
/// on the handset and uploaded hours later, so SAP's figure for a van warehouse is stale by a day's
/// trading and the daily snapshot job — which reads SAP — inherits that staleness. The van itself holds
/// the only live count, in its own ledger, and until this route there was no way for it to say so.</para>
///
/// <para>Snake-cased like every other van-sales payload, which the handset's serializer expects.</para>
/// </remarks>
public class VanSalesStockPositionRequest
{
    /// <summary>
    /// When the van counted, local wall clock with no offset. Its date is the trading day the position
    /// is filed under, for the same reason a sale's is: the day belongs to the van, not to the server.
    /// </summary>
    [JsonPropertyName("captured_at")]
    public string? CapturedAt { get; set; }

    /// <summary>
    /// The handset's own reference for this count, so a resend is recognisable as the same one.
    /// </summary>
    [JsonPropertyName("client_reference")]
    public string? ClientReference { get; set; }

    [JsonPropertyName("lines")]
    public List<VanSalesStockPositionLineRequest> Lines { get; set; } = [];
}

public class VanSalesStockPositionLineRequest
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>The batch this quantity is of, where the van tracks batches. Null folds into one row.</summary>
    [JsonPropertyName("batch")]
    public string? Batch { get; set; }

    /// <summary>What the van is carrying, in <see cref="UoMCode"/>.</summary>
    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("uom_code")]
    public string? UoMCode { get; set; }

    /// <summary>Date only. What the expiry report on the portal reads.</summary>
    [JsonPropertyName("expiry_date")]
    public string? ExpiryDate { get; set; }
}

/// <summary>What the handset is told once its position is held.</summary>
public class VanSalesStockPositionResponse
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }

    [JsonPropertyName("warehouse_code")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("trading_date")]
    public string? TradingDate { get; set; }

    [JsonPropertyName("line_count")]
    public int LineCount { get; set; }

    /// <summary>
    /// True when this van had already filed its opening position for the day and this one changed
    /// nothing. Not an error: a handset that loses the reply re-sends, and the first count of the day
    /// is the one the variance report is built on.
    /// </summary>
    [JsonPropertyName("duplicate")]
    public bool Duplicate { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
