using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.ManagementSales.Queries.GetSalesPerformanceEmailSchedule;

/// <summary>
/// Where the management email's schedule lives in <c>SystemConfigs</c>.
/// </summary>
/// <remarks>
/// Its own category beside the Shop &amp; Vending review's, because the two go to different people on
/// different days: the review is for whoever runs the counters, this is for whoever runs the business.
/// Until someone saves it no row exists and the email is off.
/// </remarks>
internal static class SalesPerformanceEmailScheduleKeys
{
    public const string Category = "SalesPerformanceEmail";
    public const string Recipients = "SalesPerformanceEmail.Recipients";
    public const string SendAsUserId = "SalesPerformanceEmail.SendAsUserId";

    /// <summary>Whether a cadence is switched on.</summary>
    public static string Enabled(string cadence) => $"SalesPerformanceEmail.Enabled.{cadence}";

    /// <summary>The last period end a cadence was sent for, so a rerun never sends a period twice.</summary>
    public static string LastSent(string cadence) => $"SalesPerformanceEmail.LastSent.{cadence}";

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
}
