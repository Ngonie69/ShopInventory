using System.Text.Json.Serialization;

namespace ShopInventory.Web.Models;

/// <summary>
/// The Web's mirror of the API's stock write-off contract.
/// </summary>
/// <remarks>
/// Hand-mirrored, like every other DTO here — there is no shared project. Nullability has to match
/// the API's or a page reads a populated response as empty.
/// </remarks>
public class CreateStockWriteOffRequest
{
    [JsonPropertyName("warehouseCode")] public string WarehouseCode { get; set; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    [JsonPropertyName("remarks")] public string? Remarks { get; set; }
    [JsonPropertyName("docDate")] public string? DocDate { get; set; }
    [JsonPropertyName("clientRequestId")] public string? ClientRequestId { get; set; }
    [JsonPropertyName("lines")] public List<CreateStockWriteOffLine> Lines { get; set; } = new();
}

public class CreateStockWriteOffLine
{
    [JsonPropertyName("itemCode")] public string ItemCode { get; set; } = string.Empty;
    [JsonPropertyName("itemDescription")] public string? ItemDescription { get; set; }
    [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
    [JsonPropertyName("uoMCode")] public string? UoMCode { get; set; }
    [JsonPropertyName("batchNumber")] public string? BatchNumber { get; set; }
    [JsonPropertyName("serialNumber")] public string? SerialNumber { get; set; }
}

public class StockWriteOffSummary
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("warehouseCode")] public string WarehouseCode { get; set; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    [JsonPropertyName("raisedByName")] public string RaisedByName { get; set; } = string.Empty;
    [JsonPropertyName("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [JsonPropertyName("lineCount")] public int LineCount { get; set; }
    [JsonPropertyName("totalQuantity")] public decimal TotalQuantity { get; set; }
    [JsonPropertyName("sapDocNum")] public int? SapDocNum { get; set; }
}

public class StockWriteOffListResponse
{
    [JsonPropertyName("items")] public List<StockWriteOffSummary> Items { get; set; } = new();
    [JsonPropertyName("totalCount")] public int TotalCount { get; set; }
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("pageSize")] public int PageSize { get; set; }
    [JsonPropertyName("statusCounts")] public Dictionary<string, int> StatusCounts { get; set; } = new();
}

public class StockWriteOffDetail
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("clientRequestId")] public string ClientRequestId { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("raisedByUserId")] public Guid RaisedByUserId { get; set; }
    [JsonPropertyName("raisedByName")] public string RaisedByName { get; set; } = string.Empty;
    [JsonPropertyName("warehouseCode")] public string WarehouseCode { get; set; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    [JsonPropertyName("remarks")] public string? Remarks { get; set; }
    [JsonPropertyName("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [JsonPropertyName("sapDocEntry")] public int? SapDocEntry { get; set; }
    [JsonPropertyName("sapDocNum")] public int? SapDocNum { get; set; }
    [JsonPropertyName("postedAtUtc")] public DateTime? PostedAtUtc { get; set; }
    [JsonPropertyName("lastAttemptedAtUtc")] public DateTime? LastAttemptedAtUtc { get; set; }
    [JsonPropertyName("lastError")] public string? LastError { get; set; }
    [JsonPropertyName("lines")] public List<StockWriteOffLine> Lines { get; set; } = new();
}

public class StockWriteOffLine
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("lineNum")] public int LineNum { get; set; }
    [JsonPropertyName("itemCode")] public string ItemCode { get; set; } = string.Empty;
    [JsonPropertyName("itemDescription")] public string? ItemDescription { get; set; }
    [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
    [JsonPropertyName("uoMCode")] public string? UoMCode { get; set; }
    [JsonPropertyName("batchNumber")] public string? BatchNumber { get; set; }
    [JsonPropertyName("serialNumber")] public string? SerialNumber { get; set; }
}

public class StockWriteOffResult
{
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("alreadyPosted")] public bool AlreadyPosted { get; set; }
    [JsonPropertyName("writeOff")] public StockWriteOffDetail WriteOff { get; set; } = new();
}

public class StockWriteOffReason
{
    [JsonPropertyName("value")] public string Value { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
}

public class StockWriteOffReasonsResponse
{
    [JsonPropertyName("reasons")] public List<StockWriteOffReason> Reasons { get; set; } = new();

    /// <summary>
    /// False when SAP itself will not record the reason, because this company database defines no
    /// reason field on the goods-issue line table. The page says so rather than implying otherwise.
    /// </summary>
    [JsonPropertyName("recordedInSap")] public bool RecordedInSap { get; set; }
}
