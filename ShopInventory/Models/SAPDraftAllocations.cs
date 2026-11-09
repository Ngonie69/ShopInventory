using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShopInventory.Models;

/// <summary>
/// A draft's lines with the batches and serial numbers allocated to them, read whole from
/// <c>Drafts(n)</c>.
/// </summary>
/// <remarks>
/// Only a by-key read with no <c>$select</c> returns a line's <c>BatchNumbers</c>. Any <c>$select</c>
/// naming <c>DocumentLines</c> gives every line an empty list, so a batch-managed draft would look
/// unallocated.
/// </remarks>
public sealed class SAPDraftAllocations
{
    [JsonPropertyName("DocEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("DocumentLines")]
    public List<SAPDraftAllocationLine>? DocumentLines { get; set; }
}

public sealed class SAPDraftAllocationLine
{
    [JsonPropertyName("LineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("ItemCode")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("Quantity")]
    public decimal Quantity { get; set; }

    /// <summary>The quantity in the inventory unit, which batch quantities are counted in. Empty when the line is entered in that unit.</summary>
    [JsonPropertyName("InventoryQuantity")]
    public decimal? InventoryQuantity { get; set; }

    [JsonPropertyName("BatchNumbers")]
    public List<SAPDraftBatchAllocation>? BatchNumbers { get; set; }

    [JsonPropertyName("SerialNumbers")]
    public List<JsonElement>? SerialNumbers { get; set; }
}

public sealed class SAPDraftBatchAllocation
{
    [JsonPropertyName("BatchNumber")]
    public string? BatchNumber { get; set; }

    [JsonPropertyName("Quantity")]
    public decimal Quantity { get; set; }
}

/// <summary>A new quantity for one draft line, and how much to move each of its batches by.</summary>
/// <remarks>
/// <c>BatchDeltas</c> are differences, not new quantities: a PATCH that names a batch the line
/// already holds adds the quantity sent to it. Measured on KEFALOS_TEST_3, 2026-11-09 — sending 24
/// to a batch holding 26 left it at 50, with or without <c>B1S-ReplaceCollectionsFieldsOnPatch</c>.
/// </remarks>
public sealed record SapDraftLineQuantityChange(
    int LineNum,
    decimal Quantity,
    IReadOnlyList<SapDraftBatchDelta> BatchDeltas);

public sealed record SapDraftBatchDelta(string BatchNumber, decimal Delta);
