using System.Text.Json.Serialization;

namespace ShopInventory.Models;

/// <summary>The SAP documents that move a warehouse's stock and are read by creation date.</summary>
public enum SapStockDocumentKind
{
    Invoice,
    CreditNote,
    StockTransfer
}

/// <summary>
/// A SAP document read for what it did to a warehouse's stock and when SAP created it.
/// </summary>
/// <remarks>
/// One shape for invoices, credit notes and stock transfers, because a stock reconciliation only needs
/// the same few facts from each: which items, how many in the inventory unit, which warehouse, the date
/// printed on the document and the moment SAP created it. The two dates differ whenever a document is
/// backdated — a day's van sales posted two days later, a load keyed in on Thursday for Monday — and
/// that difference is exactly what a morning stock count sees.
/// </remarks>
public sealed class SapStockMovementDocument
{
    [JsonPropertyName("DocEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("DocNum")]
    public int DocNum { get; set; }

    [JsonPropertyName("DocDate")]
    public string? DocDate { get; set; }

    [JsonPropertyName("CreationDate")]
    public string? CreationDate { get; set; }

    /// <summary>Creation time on SAP's clock (CAT). Marketing documents only: SAP keeps none on a transfer.</summary>
    [JsonPropertyName("DocTime")]
    public string? DocTime { get; set; }

    [JsonPropertyName("Comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("CardCode")]
    public string? CardCode { get; set; }

    [JsonPropertyName("FromWarehouse")]
    public string? FromWarehouse { get; set; }

    [JsonPropertyName("ToWarehouse")]
    public string? ToWarehouse { get; set; }

    [JsonPropertyName("DocumentLines")]
    public List<SapStockMovementLine>? DocumentLines { get; set; }

    [JsonPropertyName("StockTransferLines")]
    public List<SapStockMovementLine>? StockTransferLines { get; set; }

    /// <summary>Set by the reader, since the payload does not say which entity set it came from.</summary>
    [JsonIgnore]
    public SapStockDocumentKind Kind { get; set; }

    [JsonIgnore]
    public IReadOnlyList<SapStockMovementLine> Lines =>
        (IReadOnlyList<SapStockMovementLine>?)DocumentLines ?? StockTransferLines ?? [];
}

public sealed class SapStockMovementLine
{
    [JsonPropertyName("ItemCode")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("ItemDescription")]
    public string? ItemDescription { get; set; }

    /// <summary>In the line's own unit of measure.</summary>
    [JsonPropertyName("Quantity")]
    public double Quantity { get; set; }

    /// <summary>In the item's inventory unit — the unit stock is counted in.</summary>
    [JsonPropertyName("InventoryQuantity")]
    public double? InventoryQuantity { get; set; }

    /// <summary>The warehouse the line issues from (invoice) or receives into (credit note, transfer).</summary>
    [JsonPropertyName("WarehouseCode")]
    public string? WarehouseCode { get; set; }

    /// <summary>Transfer lines only: where the stock left.</summary>
    [JsonPropertyName("FromWarehouseCode")]
    public string? FromWarehouseCode { get; set; }

    /// <summary>
    /// The quantity in the unit a stock count is taken in. Falls back to the line quantity when SAP
    /// leaves the inventory figure empty, which it does on lines entered in the inventory unit.
    /// </summary>
    [JsonIgnore]
    public decimal StockQuantity =>
        (decimal)(InventoryQuantity is { } inventory && inventory != 0 ? inventory : Quantity);
}
