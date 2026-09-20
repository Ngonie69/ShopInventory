using System.ComponentModel.DataAnnotations;
using ShopInventory.Services;

namespace ShopInventory.Models;

/// <summary>
/// A request to issue counted stock out of one warehouse — a stock write-off.
/// </summary>
/// <remarks>
/// One warehouse for the whole document, unlike a transfer, because a goods issue has one side and a
/// write-off is always a count taken at one place.
/// </remarks>
public class CreateGoodsIssueRequest
{
    /// <summary>The warehouse the stock leaves.</summary>
    [Required(ErrorMessage = "Warehouse code is required")]
    public string WarehouseCode { get; set; } = null!;

    /// <summary>
    /// Optional posting date, <c>yyyy-MM-dd</c>. Left unset, SAP is asked for today in CAT, which is
    /// the business date this company keeps.
    /// </summary>
    public string? DocDate { get; set; }

    /// <summary>What goes in the document's <c>Comments</c>: who wrote it off and why.</summary>
    public string? Comments { get; set; }

    /// <summary>What goes in the document's <c>JournalMemo</c>.</summary>
    public string? JournalMemo { get; set; }

    /// <summary>
    /// Client-supplied idempotency key, also accepted through the <c>Idempotency-Key</c> header.
    /// </summary>
    public string? ClientRequestId { get; set; }

    /// <summary>
    /// The reference this system writes into SAP's <c>Reference2</c>, derived from
    /// <see cref="ClientRequestId"/> so a retry after a lost reply can find the document instead of
    /// issuing the stock twice.
    /// </summary>
    /// <remarks>
    /// <see cref="System.Text.Json.Serialization.JsonIgnoreAttribute"/>, and that is the guard rather
    /// than a convention — the same reasoning as <c>CreateCreditNoteRequest.SapReference</c>. A caller
    /// that could name this could name the reference another request derives, and the two documents
    /// would adopt each other.
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SapReference { get; set; }

    [Required(ErrorMessage = "At least one line item is required")]
    [MinLength(1, ErrorMessage = "At least one line item is required")]
    public List<CreateGoodsIssueLineRequest> Lines { get; set; } = new();
}

/// <summary>
/// One line of a stock write-off.
/// </summary>
public class CreateGoodsIssueLineRequest
{
    [Required(ErrorMessage = "Item code is required")]
    public string ItemCode { get; set; } = null!;

    [Range(0.00001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    /// <summary>
    /// The unit the quantity is stated in. Normalised against the item's SAP UoM before the document
    /// is built, as every other posting path does.
    /// </summary>
    public string? UoMCode { get; set; }

    /// <summary>
    /// The reason this line is being written off. Written to SAP's line-level <c>U_Reasons</c> only
    /// when the company database defines that user field on the goods-issue line table; otherwise it
    /// is kept on the local record and in the document's comments.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Which batches the units come out of. Required by SAP for a batch-managed item — a line with no
    /// selection fails the whole document, not just the line. Left empty, the batches are allocated
    /// first-expired-first-out.
    /// </summary>
    public List<TransferBatchRequest>? BatchNumbers { get; set; }

    /// <summary>
    /// Which serial numbers leave, one entry per unit: SAP counts a serial number as a single unit.
    /// </summary>
    public List<TransferSerialRequest>? SerialNumbers { get; set; }
}
