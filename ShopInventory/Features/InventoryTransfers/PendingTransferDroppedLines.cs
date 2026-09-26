using System.Text.Json;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.InventoryTransfers;

/// <summary>Reads and writes <see cref="PendingInventoryTransferEntity.DroppedLinesJson"/>.</summary>
public static class PendingTransferDroppedLines
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(IReadOnlyList<PendingTransferDroppedLine> lines)
        => JsonSerializer.Serialize(lines, Options);

    /// <summary>The dropped lines, or none when the column is empty or unreadable.</summary>
    public static IReadOnlyList<PendingTransferDroppedLine> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<PendingTransferDroppedLine>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
