using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CustomerDocuments.Delivery;

/// <summary>
/// How far the invoice scan has read: the highest SAP DocEntry it has looked at, kept in
/// <c>SystemConfigs</c> so every node's pass continues from the same place.
/// </summary>
public sealed class InvoiceScanCheckpoint
{
    public const string Key = "CustomerDocuments.InvoiceScanCheckpoint";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public int LastDocEntry { get; set; }

    /// <summary>When the scan first started: nothing posted before this is ever sent automatically.</summary>
    public DateTime InitialisedAtUtc { get; set; }

    public DateTime LastScanAtUtc { get; set; }

    /// <summary>The stored checkpoint, or null when there is none or it cannot be read.</summary>
    public static InvoiceScanCheckpoint? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            var checkpoint = JsonSerializer.Deserialize<InvoiceScanCheckpoint>(value, Json);
            return checkpoint is { LastDocEntry: >= 0 } ? checkpoint : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<InvoiceScanCheckpoint?> ReadAsync(ApplicationDbContext context, CancellationToken cancellationToken)
    {
        var value = await context.SystemConfigs
            .AsNoTracking()
            .Where(config => config.Key == Key)
            .Select(config => config.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return Parse(value);
    }

    /// <summary>Stages the checkpoint on a tracked row; the caller saves, with whatever it queued.</summary>
    public async Task StageAsync(ApplicationDbContext context, CancellationToken cancellationToken)
    {
        var row = await context.SystemConfigs
            .AsTracking()
            .FirstOrDefaultAsync(config => config.Key == Key, cancellationToken);

        if (row is null)
        {
            row = new SystemConfigEntity
            {
                Key = Key,
                ValueType = "json",
                Category = CustomerDocumentDeliveryKeys.Category,
                Description = "The last SAP invoice the automatic WhatsApp send scan has read. Delete it to start again from the newest invoice.",
                IsEditable = false
            };
            context.SystemConfigs.Add(row);
        }

        row.Value = JsonSerializer.Serialize(this, Json);
        row.UpdatedAt = LastScanAtUtc;
    }
}
