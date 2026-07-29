using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Caching;

/// <summary>
/// Postgres-backed <see cref="ISapItemUomMappingStore"/>. Opens its own scope per call so it can
/// be a singleton consumed by the transient SAP client without capturing a scoped DbContext.
/// </summary>
public sealed class SapItemUomMappingStore(
    IServiceScopeFactory scopeFactory,
    ILogger<SapItemUomMappingStore> logger) : ISapItemUomMappingStore
{
    public async Task<IReadOnlyDictionary<SapItemUomKey, (string? UoMCode, int UoMEntry)>> GetAsync(
        IReadOnlyCollection<SapItemUomKey> keys,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<SapItemUomKey, (string? UoMCode, int UoMEntry)>();
        if (keys.Count == 0)
        {
            return resolved;
        }

        try
        {
            // Keys are stored upper-cased, and Postgres compares text case-sensitively, so the
            // lookup has to be normalised the same way the write is.
            var requested = keys.Select(key => SapItemUomKey.For(key.ItemCode, key.RequestedUomCode)).ToHashSet();

            // Filtering on the pair would produce an OR chain the index cannot serve, so the item
            // codes narrow it in SQL and the requested-UoM half of the key is matched in memory.
            var itemCodes = requested
                .Select(key => key.ItemCode)
                .Distinct()
                .ToList();

            using var scope = scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var rows = await context.SapItemUomMappings
                .AsNoTracking()
                .Where(mapping => itemCodes.Contains(mapping.ItemCode))
                .Select(mapping => new
                {
                    mapping.ItemCode,
                    mapping.RequestedUomCode,
                    mapping.UoMCode,
                    mapping.UoMEntry
                })
                .ToListAsync(cancellationToken);

            foreach (var row in rows)
            {
                var key = SapItemUomKey.For(row.ItemCode, row.RequestedUomCode);
                if (requested.Contains(key))
                {
                    resolved[key] = (row.UoMCode, row.UoMEntry);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A read failure only means the resolutions get rebuilt from SAP this time.
            logger.LogWarning(ex, "Failed to read stored SAP item UoM mappings for {KeyCount} key(s)", keys.Count);
        }

        return resolved;
    }

    public async Task SaveAsync(
        IReadOnlyCollection<(SapItemUomKey Key, string? UoMCode, int UoMEntry)> mappings,
        CancellationToken cancellationToken)
    {
        if (mappings.Count == 0)
        {
            return;
        }

        try
        {
            var deduplicated = mappings
                .GroupBy(mapping => SapItemUomKey.For(mapping.Key.ItemCode, mapping.Key.RequestedUomCode))
                .ToDictionary(group => group.Key, group => group.Last());

            var itemCodes = deduplicated.Keys
                .Select(key => key.ItemCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            using var scope = scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var existing = await context.SapItemUomMappings
                .Where(mapping => itemCodes.Contains(mapping.ItemCode))
                .ToListAsync(cancellationToken);

            var existingByKey = existing.ToDictionary(
                mapping => SapItemUomKey.For(mapping.ItemCode, mapping.RequestedUomCode));

            var now = DateTime.UtcNow;
            foreach (var (key, mapping) in deduplicated)
            {
                if (existingByKey.TryGetValue(key, out var row))
                {
                    row.UoMCode = mapping.UoMCode;
                    row.UoMEntry = mapping.UoMEntry;
                    row.ResolvedAtUtc = now;
                    continue;
                }

                context.SapItemUomMappings.Add(new SapItemUomMappingEntity
                {
                    ItemCode = key.ItemCode,
                    RequestedUomCode = key.RequestedUomCode,
                    UoMCode = mapping.UoMCode,
                    UoMEntry = mapping.UoMEntry,
                    ResolvedAtUtc = now
                });
            }

            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Losing a write to a concurrent resolver of the same item is harmless — both derived
            // the value from the same SAP data — and no persistence failure may fail a SAP post.
            logger.LogWarning(ex, "Failed to persist {MappingCount} SAP item UoM mapping(s)", mappings.Count);
        }
    }
}
