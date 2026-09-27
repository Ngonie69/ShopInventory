using System.Text.Json.Serialization;

namespace ShopInventory.Models;

/// <summary>
/// An SAP Inventory Counting document (<c>OINC</c>): what was on the books in a warehouse on the
/// count date, and what somebody counted there.
/// </summary>
/// <remarks>
/// Read-only here. A count is closed in SAP by copying it to an Inventory Posting, which is B1's
/// job, not this application's.
/// </remarks>
public class InventoryCounting
{
    [JsonPropertyName("DocumentEntry")]
    public int DocumentEntry { get; set; }

    [JsonPropertyName("DocumentNumber")]
    public int DocumentNumber { get; set; }

    /// <summary>ISO timestamp at midnight, e.g. <c>2023-12-31T00:00:00Z</c>.</summary>
    [JsonPropertyName("CountDate")]
    public string? CountDate { get; set; }

    /// <summary><c>HH:mm:ss</c>.</summary>
    [JsonPropertyName("CountTime")]
    public string? CountTime { get; set; }

    /// <summary><c>ctUser</c> or <c>ctEmployee</c>: which table <see cref="SingleCounterID"/> keys.</summary>
    [JsonPropertyName("SingleCounterType")]
    public string? SingleCounterType { get; set; }

    [JsonPropertyName("SingleCounterID")]
    public int? SingleCounterID { get; set; }

    /// <summary><c>cdsOpen</c> or <c>cdsClosed</c>.</summary>
    [JsonPropertyName("DocumentStatus")]
    public string? DocumentStatus { get; set; }

    [JsonPropertyName("Remarks")]
    public string? Remarks { get; set; }

    [JsonPropertyName("Reference2")]
    public string? Reference2 { get; set; }

    /// <summary><c>ctSingleCounter</c> or <c>ctMultipleCounters</c>.</summary>
    [JsonPropertyName("CountingType")]
    public string? CountingType { get; set; }

    [JsonPropertyName("InventoryCountingLines")]
    public List<InventoryCountingLine>? InventoryCountingLines { get; set; }
}

public class InventoryCountingLine
{
    [JsonPropertyName("LineNumber")]
    public int LineNumber { get; set; }

    /// <summary>The row number B1 shows on screen, zero-based.</summary>
    [JsonPropertyName("VisualOrder")]
    public int? VisualOrder { get; set; }

    [JsonPropertyName("ItemCode")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("ItemDescription")]
    public string? ItemDescription { get; set; }

    [JsonPropertyName("WarehouseCode")]
    public string? WarehouseCode { get; set; }

    /// <summary>What SAP had on the books in the warehouse on the count date, in inventory units.</summary>
    [JsonPropertyName("InWarehouseQuantity")]
    public decimal InWarehouseQuantity { get; set; }

    /// <summary>
    /// <c>tYES</c> once the line has been counted. An uncounted line carries a counted quantity and
    /// variance of zero, which must not be read as "counted nothing".
    /// </summary>
    [JsonPropertyName("Counted")]
    public string? Counted { get; set; }

    /// <summary>The counted quantity in inventory units.</summary>
    [JsonPropertyName("CountedQuantity")]
    public decimal CountedQuantity { get; set; }

    /// <summary>SAP's own: counted minus in-warehouse, in inventory units.</summary>
    [JsonPropertyName("Variance")]
    public decimal Variance { get; set; }

    [JsonPropertyName("UoMCode")]
    public string? UoMCode { get; set; }

    /// <summary><c>clsOpen</c> or <c>clsClosed</c>.</summary>
    [JsonPropertyName("LineStatus")]
    public string? LineStatus { get; set; }
}
