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

/// <summary>
/// What the van is carrying now, as far as this system can tell.
/// </summary>
/// <remarks>
/// <para><b>Why a van needs to be told what it has.</b> The handset holds the live count and posts it
/// once each morning, and from then on nothing here could answer the question back. That is fine
/// until the handset cannot: a reinstall, a replacement device, or a handover to the next rep leaves
/// somebody standing in front of a customer with no product list at all, and the fallback — SAP's
/// figure for a van warehouse — is a day of trading out of date. This is the answer that can be
/// rebuilt from what the platform already knows.</para>
///
/// <para><b>It is a statement, not an instruction.</b> The handset's own count is the better number
/// while the handset has one, because it includes sales that have not been uploaded yet. This is for
/// the case where there is nothing to compare against, and for a rep who wants to see whether the two
/// agree. Nothing here refuses a sale.</para>
/// </remarks>
public class VanSalesStockPositionResult
{
    [JsonPropertyName("warehouse_code")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("trading_date")]
    public string? TradingDate { get; set; }

    /// <summary>
    /// False when no opening count was filed for this van today, in which case <c>lines</c> is empty
    /// and means "not known" rather than "nothing on the van".
    /// </summary>
    /// <remarks>
    /// The distinction is the whole safety of this route. An absent count rendered as an empty list
    /// would tell a rep the van is empty, which is both wrong and the kind of wrong that stops a
    /// day's selling.
    /// </remarks>
    [JsonPropertyName("counted")]
    public bool Counted { get; set; }

    [JsonPropertyName("line_count")]
    public int LineCount { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("lines")]
    public List<VanSalesStockPositionResultLine> Lines { get; set; } = [];
}

/// <summary>One item's position, and the arithmetic behind it.</summary>
/// <remarks>
/// The three components are returned alongside the total on purpose. A rep who disagrees with the
/// figure can see which part they disagree with — the morning count, the load that came out since, or
/// the sales the platform has received — and that is a question somebody can answer. A single number
/// is one they can only dispute.
/// </remarks>
public class VanSalesStockPositionResultLine
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>What the handset counted onto the van this morning.</summary>
    [JsonPropertyName("opening_quantity")]
    public decimal OpeningQuantity { get; set; }

    /// <summary>Stock transferred in or out since, as the transfer listener reported it.</summary>
    [JsonPropertyName("transferred_quantity")]
    public decimal TransferredQuantity { get; set; }

    /// <summary>What this system has received sales for today, posted to SAP or not.</summary>
    [JsonPropertyName("sold_quantity")]
    public decimal SoldQuantity { get; set; }

    /// <summary>Opening, plus transfers, less sales. Never below zero.</summary>
    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }
}
