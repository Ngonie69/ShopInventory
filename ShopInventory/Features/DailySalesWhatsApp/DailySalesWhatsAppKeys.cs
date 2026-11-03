using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppSettings;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DailySalesWhatsApp;

/// <summary>
/// Where the daily sales WhatsApp report keeps its settings and what it has sent, in <c>SystemConfigs</c>.
/// </summary>
/// <remarks>
/// Until an admin saves it no row exists and the report is off. The two row lists are stored as compact
/// JSON in one value each; a value holds at most <see cref="MaxValueLength"/> characters, which the save
/// checks so a long list is refused rather than cut short.
/// </remarks>
internal static class DailySalesWhatsAppKeys
{
    public const string Category = "DailySalesWhatsApp";
    public const string Enabled = "DailySalesWhatsApp.Enabled";
    public const string Recipients = "DailySalesWhatsApp.Recipients";
    public const string ShopWarehouseCode = "DailySalesWhatsApp.ShopWarehouseCode";
    public const string ShopHeading = "DailySalesWhatsApp.ShopHeading";
    public const string VanRows = "DailySalesWhatsApp.VanRows";
    public const string ProductRows = "DailySalesWhatsApp.ProductRows";
    public const string UpdatedBy = "DailySalesWhatsApp.UpdatedBy";

    /// <summary>The trading day the numbers in <see cref="SentTo"/> were sent the report for.</summary>
    public const string SentFor = "DailySalesWhatsApp.SentFor";

    /// <summary>Who has the report for <see cref="SentFor"/>, so a later pass sends only to the rest.</summary>
    public const string SentTo = "DailySalesWhatsApp.SentTo";

    /// <summary>The width of <c>SystemConfigs.Value</c>.</summary>
    public const int MaxValueLength = 4000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<Dictionary<string, (string? Value, DateTime UpdatedAt)>> ReadAsync(
        ApplicationDbContext db, CancellationToken cancellationToken) =>
        (await db.SystemConfigs
            .AsNoTracking()
            .Where(config => config.Category == Category)
            .Select(config => new { config.Key, config.Value, config.UpdatedAt })
            .ToListAsync(cancellationToken))
        .ToDictionary(config => config.Key, config => (config.Value, config.UpdatedAt));

    /// <summary>Stages a value; the caller saves.</summary>
    public static async Task StageAsync(
        ApplicationDbContext db, string key, string valueType, string? value, string description, CancellationToken cancellationToken)
    {
        var row = await db.SystemConfigs.FirstOrDefaultAsync(config => config.Key == key, cancellationToken);
        if (row is null)
        {
            row = new SystemConfigEntity
            {
                Key = key,
                ValueType = valueType,
                Category = Category,
                Description = description,
                IsEditable = true
            };
            db.SystemConfigs.Add(row);
        }

        row.Value = value;
        row.UpdatedAt = DateTime.UtcNow;
    }

    public static string SerializeRows(IEnumerable<DailySalesWhatsAppRow> rows) =>
        JsonSerializer.Serialize(rows.Select(row => new StoredRow(row.Label, row.Codes.ToList<string?>(), row.ShowArea ? true : null)), Json);

    public static List<DailySalesWhatsAppRow> DeserializeRows(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<StoredRow>>(value, Json) ?? [])
                .Where(row => !string.IsNullOrWhiteSpace(row.L))
                .Select(row => new DailySalesWhatsAppRow(row.L!.Trim(), Codes(row.C), row.A == true))
                .ToList();
        }
        catch (JsonException)
        {
            // A row an admin edited by hand into something unreadable reads as no rows, never as an error
            // that stops the morning send: the van half falls back to every route under its own name.
            return [];
        }
    }

    /// <summary>Numbers are stored as E.164 (<c>+263771234567</c>), comma-separated.</summary>
    public static List<string> Numbers(string? value) =>
        (value ?? string.Empty)
            .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static bool Flag(IReadOnlyDictionary<string, (string? Value, DateTime UpdatedAt)> rows, string key) =>
        rows.TryGetValue(key, out var row) && bool.TryParse(row.Value, out var on) && on;

    public static string? Text(IReadOnlyDictionary<string, (string? Value, DateTime UpdatedAt)> rows, string key) =>
        rows.TryGetValue(key, out var row) && !string.IsNullOrWhiteSpace(row.Value) ? row.Value.Trim() : null;

    public static DateTime? Date(IReadOnlyDictionary<string, (string? Value, DateTime UpdatedAt)> rows, string key) =>
        rows.TryGetValue(key, out var row)
        && DateTime.TryParseExact(row.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    public static string DateValue(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Codes trimmed, upper-cased and de-duplicated, in the order given.</summary>
    public static List<string> Codes(IEnumerable<string?>? codes) =>
        (codes ?? [])
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Short property names: the whole list has to fit in one 4,000-character value.</summary>
    private sealed record StoredRow(string? L, List<string?>? C, bool? A);
}
