using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetPostingDatePolicy;

/// <summary>
/// Where the posting-date switch lives in <c>SystemConfigs</c>, and how it is read and written.
/// </summary>
/// <remarks>
/// <c>SystemConfigs</c> for the reason the review schedule is there: an admin flips it from Web → Settings and
/// the next sale on every node honours it, with no deploy. Until someone saves it no row exists and the
/// switch is off — a till posts on the day it sells, which is what it always did.
/// </remarks>
internal static class PostingDatePolicyKeys
{
    public const string Category = "DesktopSales";
    public const string AllowCustomPostingDate = "DesktopSales.AllowCustomPostingDate";
    public const string ChangedBy = "DesktopSales.AllowCustomPostingDate.ChangedBy";

    public static async Task<bool> IsAllowedAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var value = await db.SystemConfigs
            .AsNoTracking()
            .Where(config => config.Key == AllowCustomPostingDate)
            .Select(config => config.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return bool.TryParse(value, out var on) && on;
    }

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
