using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// The warehouses the daily stock snapshot covers, as every reader of that list should see it.
/// </summary>
/// <remarks>
/// <para>
/// Held in <c>SystemConfigs</c> so it can be edited from the Local stock page and take effect on the
/// next read, on every node, without a deploy. Until someone saves it the row does not exist and
/// <see cref="DailyStockSettings.MonitoredWarehouses"/> from appsettings.json decides — which is also
/// what every test that builds a context without the row gets.
/// </para>
/// <para>
/// Read through <see cref="ReadAsync"/> everywhere, never off the options directly: a reader left on
/// the options would go on snapshotting, adjusting or reconciling the configured list after the saved
/// one had moved on, and nothing would say so.
/// </para>
/// </remarks>
public static class MonitoredWarehouseList
{
    /// <summary>The <c>SystemConfigs</c> key holding the saved list, a JSON array of warehouse codes.</summary>
    public const string ConfigKey = "DailyStock.MonitoredWarehouses";

    public static async Task<List<string>> ReadAsync(
        ApplicationDbContext context,
        DailyStockSettings settings,
        CancellationToken cancellationToken = default) =>
        (await ReadStateAsync(context, settings, cancellationToken)).Warehouses;

    public static async Task<MonitoredWarehouseListState> ReadStateAsync(
        ApplicationDbContext context,
        DailyStockSettings settings,
        CancellationToken cancellationToken = default)
    {
        var row = await context.SystemConfigs
            .AsNoTracking()
            .Where(config => config.Key == ConfigKey)
            .Select(config => new { config.Value, config.UpdatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        var saved = row?.Value is { Length: > 0 } json ? Parse(json) : null;

        // A saved list that is empty or unreadable is treated as absent rather than obeyed. Obeying it
        // would snapshot nothing and leave every till refusing every sale, which is the failure
        // DailyStockSettingsValidation refuses at startup for the configured list.
        return saved is { Count: > 0 }
            ? new MonitoredWarehouseListState(saved, row!.UpdatedAt)
            : new MonitoredWarehouseListState(Normalise(settings.MonitoredWarehouses), null);
    }

    public static async Task<MonitoredWarehouseListState> SaveAsync(
        ApplicationDbContext context,
        IEnumerable<string> warehouses,
        Guid? updatedByUserId,
        CancellationToken cancellationToken = default)
    {
        var list = Normalise(warehouses)
            .Select(code => code.ToUpperInvariant())
            .Order(StringComparer.Ordinal)
            .ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("At least one warehouse has to be monitored.", nameof(warehouses));
        }

        var now = DateTime.UtcNow;
        var row = await context.SystemConfigs.FirstOrDefaultAsync(config => config.Key == ConfigKey, cancellationToken);

        if (row is null)
        {
            row = new SystemConfigEntity
            {
                Key = ConfigKey,
                ValueType = "json",
                Category = "DailyStock",
                Description =
                    "Warehouses the 07:00 stock snapshot covers. A till selling from a warehouse not on "
                    + "this list reads zero stock and refuses every sale. Edited from the Local stock page.",
                IsEditable = true
            };
            context.SystemConfigs.Add(row);
        }

        row.Value = JsonSerializer.Serialize(list);
        row.UpdatedAt = now;
        row.UpdatedByUserId = updatedByUserId;

        await context.SaveChangesAsync(cancellationToken);

        return new MonitoredWarehouseListState(list, now);
    }

    /// <summary>
    /// Blanks dropped, codes trimmed and each listed once, in the order given — the configured list
    /// is walked in its written order by the snapshot job, and this does not change that.
    /// </summary>
    public static List<string> Normalise(IEnumerable<string?> warehouses) =>
        warehouses
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string>? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string?>>(json) is { } codes ? Normalise(codes) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <param name="Warehouses">The warehouses in force.</param>
/// <param name="UpdatedAtUtc">When the list was last saved; null while appsettings.json decides.</param>
public sealed record MonitoredWarehouseListState(List<string> Warehouses, DateTime? UpdatedAtUtc);
