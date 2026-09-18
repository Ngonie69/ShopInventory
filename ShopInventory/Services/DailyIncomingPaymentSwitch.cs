using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Whether desktop sales get their daily incoming payment, or post invoices only.
/// </summary>
/// <remarks>
/// <para>
/// Held in <c>SystemConfigs</c> rather than configuration so that an admin can flip it from Web →
/// Settings and have it take effect on the job's next run, on every node, without a restart or a
/// deploy. <see cref="DailyIncomingPaymentJob"/> reads it at the start of every run.
/// </para>
/// <para>
/// Until someone saves it the row does not exist, and <see cref="DesktopSalePostingSettings.DailyPaymentEnabled"/>
/// decides (off).
/// </para>
/// </remarks>
public sealed class DailyIncomingPaymentSwitch(
    ApplicationDbContext context,
    IOptions<DesktopSalePostingSettings> settings)
{
    /// <summary>The <c>SystemConfigs</c> key holding the switch.</summary>
    public const string ConfigKey = "DesktopSalePosting.DailyPaymentEnabled";

    public async Task<DailyIncomingPaymentSwitchState> GetAsync(CancellationToken cancellationToken = default)
    {
        var row = await context.SystemConfigs
            .AsNoTracking()
            .Where(config => config.Key == ConfigKey)
            .Select(config => new { config.Value, config.UpdatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        return row is not null && bool.TryParse(row.Value, out var enabled)
            ? new DailyIncomingPaymentSwitchState(enabled, row.UpdatedAt)
            : new DailyIncomingPaymentSwitchState(settings.Value.DailyPaymentEnabled, null);
    }

    public async Task<DailyIncomingPaymentSwitchState> SetAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var row = await context.SystemConfigs.FirstOrDefaultAsync(config => config.Key == ConfigKey, cancellationToken);

        if (row is null)
        {
            row = new SystemConfigEntity
            {
                Key = ConfigKey,
                ValueType = "bool",
                Category = "DesktopSalePosting",
                Description =
                    "Whether till, vending and consolidated desktop invoices are settled by one incoming "
                    + "payment per customer per day. Off: invoices are posted to SAP and left open.",
                IsEditable = true
            };
            context.SystemConfigs.Add(row);
        }

        row.Value = enabled ? "true" : "false";
        row.UpdatedAt = now;

        await context.SaveChangesAsync(cancellationToken);

        return new DailyIncomingPaymentSwitchState(enabled, now);
    }
}

/// <param name="Enabled">Whether the daily payment posts.</param>
/// <param name="UpdatedAtUtc">When it was last saved from Settings; null while the configured default applies.</param>
public sealed record DailyIncomingPaymentSwitchState(bool Enabled, DateTime? UpdatedAtUtc);
