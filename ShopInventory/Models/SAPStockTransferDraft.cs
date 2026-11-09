using System.Text.Json.Serialization;

namespace ShopInventory.Models;

/// <summary>
/// An inventory transfer draft (ODRF, <c>ObjType 67</c>) as the Service Layer's <c>StockTransferDrafts</c>
/// set returns it: the document SAP holds while its approval procedure decides a transfer keyed in the
/// B1 client, and the one <c>SaveDraftToDocument</c> turns into the real transfer.
/// </summary>
/// <remarks>
/// <para>The set is not limited to transfers. It is a view over every draft, and an unfiltered read
/// of it on KEFALOS_TEST_3 returned goods-receipt-PO drafts (<c>DocObjectCode "20"</c>) first. Every
/// read filters on <see cref="DocObjectCode"/>.</para>
///
/// <para>A line's <see cref="SAPStockTransferDraftLine.BatchNumbers"/> come back only from a read that
/// returns one document with no <c>$select</c>. A filtered read of several drafts, or any read whose
/// <c>$select</c> names <c>StockTransferLines</c>, returns every line with an empty batch list — which
/// would make every batch-managed draft look unallocated. Measured 2026-11-06.</para>
/// </remarks>
public class SAPStockTransferDraft
{
    [JsonPropertyName("DocEntry")]
    public int DocEntry { get; set; }

    /// <summary>The draft's own number. Drafts and transfers number from different series.</summary>
    [JsonPropertyName("DocNum")]
    public int DocNum { get; set; }

    [JsonPropertyName("DocDate")]
    public string? DocDate { get; set; }

    [JsonPropertyName("DueDate")]
    public string? DueDate { get; set; }

    [JsonPropertyName("CreationDate")]
    public string? CreationDate { get; set; }

    [JsonPropertyName("FromWarehouse")]
    public string? FromWarehouse { get; set; }

    [JsonPropertyName("ToWarehouse")]
    public string? ToWarehouse { get; set; }

    [JsonPropertyName("Comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("JournalMemo")]
    public string? JournalMemo { get; set; }

    /// <summary><c>bost_Open</c> while it waits; <c>bost_Close</c> once it has been added.</summary>
    [JsonPropertyName("DocumentStatus")]
    public string? DocumentStatus { get; set; }

    /// <summary>One of <see cref="SapStockTransferAuthorizationStatuses"/> — the <c>sas*</c> enum, not the documents' <c>das*</c>.</summary>
    [JsonPropertyName("AuthorizationStatus")]
    public string? AuthorizationStatus { get; set; }

    /// <summary>"67" for an inventory transfer draft.</summary>
    [JsonPropertyName("DocObjectCode")]
    public string? DocObjectCode { get; set; }

    [JsonPropertyName("AttachmentEntry")]
    public int? AttachmentEntry { get; set; }

    [JsonPropertyName("StockTransferLines")]
    public List<SAPStockTransferDraftLine>? StockTransferLines { get; set; }
}

public class SAPStockTransferDraftLine
{
    [JsonPropertyName("LineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("ItemCode")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("ItemDescription")]
    public string? ItemDescription { get; set; }

    /// <summary>In the line's unit of measure (<see cref="UoMCode"/>).</summary>
    [JsonPropertyName("Quantity")]
    public decimal Quantity { get; set; }

    /// <summary>The same quantity in the item's inventory unit — what the stock checks count in.</summary>
    [JsonPropertyName("InventoryQuantity")]
    public decimal? InventoryQuantity { get; set; }

    [JsonPropertyName("FromWarehouseCode")]
    public string? FromWarehouseCode { get; set; }

    /// <summary>The line's destination warehouse.</summary>
    [JsonPropertyName("WarehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("UoMCode")]
    public string? UoMCode { get; set; }

    /// <summary>
    /// What the line was copied from: <c>InventoryTransferRequest</c> for a line copied from a transfer
    /// request in the B1 client, <c>Default</c> for one keyed directly.
    /// </summary>
    [JsonPropertyName("BaseType")]
    public string? BaseType { get; set; }

    /// <summary>The transfer request's DocEntry when <see cref="BaseType"/> names one.</summary>
    [JsonPropertyName("BaseEntry")]
    public int? BaseEntry { get; set; }

    [JsonPropertyName("BaseLine")]
    public int? BaseLine { get; set; }

    [JsonPropertyName("BatchNumbers")]
    public List<InventoryTransferLineBatch>? BatchNumbers { get; set; }

    [JsonPropertyName("SerialNumbers")]
    public List<SAPStockTransferDraftSerial>? SerialNumbers { get; set; }
}

public class SAPStockTransferDraftSerial
{
    [JsonPropertyName("SystemSerialNumber")]
    public int? SystemSerialNumber { get; set; }

    [JsonPropertyName("InternalSerialNumber")]
    public string? InternalSerialNumber { get; set; }

    [JsonPropertyName("Quantity")]
    public decimal Quantity { get; set; }
}

/// <summary>Whether SAP manages an item by batch or by serial number — what a transfer line must pick.</summary>
public sealed record SapItemManagement(string ItemCode, bool ManagesBatches, bool ManagesSerials);

/// <summary>
/// What the queue of approved transfer drafts is narrowed to, on top of the caller's warehouse scope.
/// Every field is a draft header field, so SAP applies it to the rows and the count alike.
/// </summary>
/// <remarks>
/// The approval stage and the originator are deliberately absent: they live on the approval request,
/// not the draft, and SAP cannot filter one entity set by another's fields in one read.
/// </remarks>
public sealed record SapTransferDraftFilter
{
    /// <summary>Only drafts leaving or entering this warehouse.</summary>
    public string? Warehouse { get; init; }

    /// <summary>Only drafts leaving one of these warehouses. Empty or null does not narrow.</summary>
    public IReadOnlyCollection<string>? FromWarehouses { get; init; }

    /// <summary>Only drafts entering one of these warehouses. Empty or null does not narrow.</summary>
    public IReadOnlyCollection<string>? ToWarehouses { get; init; }

    /// <summary>Only drafts created on or after this day (SAP's <c>CreationDate</c>).</summary>
    public DateTime? CreatedFrom { get; init; }

    /// <summary>Only drafts created on or before this day.</summary>
    public DateTime? CreatedTo { get; init; }

    /// <summary>Only drafts that carry an attachment.</summary>
    public bool AttachedOnly { get; init; }

    /// <summary>Only the draft with this number — the one the B1 client shows.</summary>
    public int? DraftNum { get; init; }
}
