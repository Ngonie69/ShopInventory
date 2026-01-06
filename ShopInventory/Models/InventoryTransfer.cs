using System.Text.Json.Serialization;

namespace ShopInventory.Models;

public class InventoryTransfer
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

    [JsonPropertyName("StockTransferLines")]
    public List<InventoryTransferLine>? StockTransferLines { get; set; }
}

public class InventoryTransferLine
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

public class SAPResponse<T>
{
    [JsonPropertyName("value")]
    public List<T>? Value { get; set; }

    [JsonPropertyName("odata.nextLink")]
    public string? NextLink { get; set; }
}

public class LoginRequest
{
    [JsonPropertyName("CompanyDB")]
    public string CompanyDB { get; set; } = string.Empty;

    [JsonPropertyName("UserName")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("Password")]
    public string Password { get; set; } = string.Empty;
}

public class LoginResponse
{
    [JsonPropertyName("SessionId")]
    public string? SessionId { get; set; }
}
