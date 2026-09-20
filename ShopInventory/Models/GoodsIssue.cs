using System.Text.Json.Serialization;

namespace ShopInventory.Models;

/// <summary>
/// A SAP Business One Goods Issue — the document that takes stock out of a warehouse without a
/// business partner on the other side of it.
/// </summary>
/// <remarks>
/// <para>
/// The entity set is <c>InventoryGenExits</c>, and its entity type is <c>Document</c> — the same type
/// as <c>Invoices</c> and <c>CreditNotes</c>, not the <c>StockTransfer</c> type behind
/// <see cref="InventoryTransfer"/>. That is why the lines are <c>DocumentLines</c> and carry a single
/// <c>WarehouseCode</c> rather than a pair: a goods issue has one side.
/// </para>
/// <para>
/// No <c>CardCode</c>: it is not a business-partner document, and sending one is how a goods issue
/// gets mistaken for a delivery. No per-line price either — omitted so SAP values the issue at the
/// item's own cost, which is what the write-off journal must be worth.
/// </para>
/// </remarks>
public class GoodsIssue
{
    [JsonPropertyName("DocEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("DocNum")]
    public int DocNum { get; set; }

    [JsonPropertyName("DocDate")]
    public string? DocDate { get; set; }

    [JsonPropertyName("Comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("JournalMemo")]
    public string? JournalMemo { get; set; }

    /// <summary>
    /// The reference this system writes so a retry after a lost reply can find the document it
    /// already created, rather than issuing the stock a second time.
    /// </summary>
    [JsonPropertyName("Reference2")]
    public string? Reference2 { get; set; }

    [JsonPropertyName("DocTotal")]
    public decimal DocTotal { get; set; }

    [JsonPropertyName("DocumentStatus")]
    public string? DocumentStatus { get; set; }

    [JsonPropertyName("Cancelled")]
    public string? Cancelled { get; set; }

    [JsonPropertyName("DocumentLines")]
    public List<GoodsIssueLine>? DocumentLines { get; set; }
}

/// <summary>
/// One line of a <see cref="GoodsIssue"/>: what left, how much of it, and out of where.
/// </summary>
public class GoodsIssueLine
{
    [JsonPropertyName("LineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("ItemCode")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("ItemDescription")]
    public string? ItemDescription { get; set; }

    [JsonPropertyName("Quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("WarehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("UoMCode")]
    public string? UoMCode { get; set; }

    /// <summary>
    /// What SAP valued the line at. Read back rather than sent: the write-off value is the item's
    /// cost, and SAP is the only thing that knows it.
    /// </summary>
    [JsonPropertyName("LineTotal")]
    public decimal LineTotal { get; set; }

    /// <summary>
    /// The G/L account SAP's own item and warehouse determination charged the issue to. Read-only
    /// here — the payload omits it deliberately so that determination is what decides.
    /// </summary>
    [JsonPropertyName("AccountCode")]
    public string? AccountCode { get; set; }
}
