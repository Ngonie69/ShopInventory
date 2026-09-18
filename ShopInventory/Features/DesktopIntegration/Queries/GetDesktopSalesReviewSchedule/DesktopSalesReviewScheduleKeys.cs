using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewSchedule;

/// <summary>
/// Where the review email's schedule lives in <c>SystemConfigs</c>, and how it is read and written.
/// </summary>
/// <remarks>
/// <c>SystemConfigs</c> rather than configuration for the reason the daily payment switch is there: an
/// admin changes it from Web → Settings and the next run honours it, on every node, with no deploy. Until
/// someone saves it no row exists and the review email is off.
/// </remarks>
internal static class DesktopSalesReviewScheduleKeys
{
    public const string Category = "DesktopSalesReview";
    public const string WeeklyEnabled = "DesktopSalesReview.WeeklyEnabled";
    public const string MonthlyEnabled = "DesktopSalesReview.MonthlyEnabled";
    public const string Recipients = "DesktopSalesReview.Recipients";
    public const string SendAsUserId = "DesktopSalesReview.SendAsUserId";

    /// <summary>The last period end each cadence was sent for, so a rerun never sends a period twice.</summary>
    public static string LastSent(string cadence) => $"DesktopSalesReview.LastSent.{cadence}";

    public static async Task<Dictionary<string, (string? Value, DateTime? UpdatedAt)>> ReadAsync(
        ApplicationDbContext db, CancellationToken cancellationToken) =>
        (await db.SystemConfigs
            .AsNoTracking()
            .Where(config => config.Category == Category)
            .Select(config => new { config.Key, config.Value, config.UpdatedAt })
            .ToListAsync(cancellationToken))
        .ToDictionary(config => config.Key, config => (config.Value, (DateTime?)config.UpdatedAt));

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

    public static bool Flag(IReadOnlyDictionary<string, (string? Value, DateTime? UpdatedAt)> rows, string key) =>
        rows.TryGetValue(key, out var row) && bool.TryParse(row.Value, out var on) && on;

    public static List<string> Addresses(string? value) =>
        (value ?? string.Empty)
            .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static DateTime? Date(IReadOnlyDictionary<string, (string? Value, DateTime? UpdatedAt)> rows, string key) =>
        rows.TryGetValue(key, out var row)
        && DateTime.TryParseExact(row.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
}
