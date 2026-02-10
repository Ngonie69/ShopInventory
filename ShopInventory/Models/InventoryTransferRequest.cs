using System.Text.Json.Serialization;

namespace ShopInventory.Models;

/// <summary>
/// Represents an Inventory Transfer Request in SAP Business One.
/// Transfer Requests are draft documents that require approval before becoming actual transfers.
/// </summary>
public class InventoryTransferRequest
{
    [JsonPropertyName("DocEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("DocNum")]
    public int DocNum { get; set; }

    [JsonPropertyName("DocDate")]
    public string? DocDate { get; set; }

    [JsonPropertyName("DueDate")]
    public string? DueDate { get; set; }

    [JsonPropertyName("FromWarehouse")]
    public string? FromWarehouse { get; set; }

    [JsonPropertyName("ToWarehouse")]
    public string? ToWarehouse { get; set; }

    [JsonPropertyName("Comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("JournalMemo")]
    public string? JournalMemo { get; set; }

    [JsonPropertyName("DocumentStatus")]
    public string? DocumentStatus { get; set; }

    [JsonPropertyName("RequesterEmail")]
    public string? RequesterEmail { get; set; }

    [JsonPropertyName("RequesterName")]
    public string? RequesterName { get; set; }

    [JsonPropertyName("RequesterBranch")]
    public int? RequesterBranch { get; set; }

    [JsonPropertyName("RequesterDepartment")]
    public int? RequesterDepartment { get; set; }

    [JsonPropertyName("StockTransferLines")]
    public List<InventoryTransferRequestLine>? StockTransferLines { get; set; }
}

/// <summary>
/// Line item for an Inventory Transfer Request
/// </summary>
public class InventoryTransferRequestLine
{
    [JsonPropertyName("LineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("ItemCode")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("ItemDescription")]
    public string? ItemDescription { get; set; }

    [JsonPropertyName("Quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("FromWarehouseCode")]
    public string? FromWarehouseCode { get; set; }

    [JsonPropertyName("WarehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("UoMCode")]
    public string? UoMCode { get; set; }
}
