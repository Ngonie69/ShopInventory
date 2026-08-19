using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Health;

/// <summary>
/// Reports van receipts that have stopped on their way to ZIMRA.
/// </summary>
/// <remarks>
/// <see cref="VanSalesSignedReceiptIngestService"/> already detects every one of these conditions and
/// describes it precisely in the log, but a log line is not a signal: the Exception Center and the
/// fiscalisation console show them only to someone who goes looking. This check is the missing push —
/// it is the one path by which a stopped handset reaches <c>SystemFailureAlertJob</c> and its
/// recipients.
///
/// Why that matters more than an ordinary backlog: a handset holds its whole queue behind its first
/// failure, because the platform accepts receipt N+1 only once it holds N. So one stuck receipt is not
/// one missing receipt — it is every receipt that handset signs for the rest of the day, and the
/// remedy is a person reconciling the chain, not a retry. Unhealthy is therefore the right severity
/// for a single row: nothing drains it on its own.
///
/// The backlog thresholds catch the opposite shape of failure — the platform unreachable, or its
/// ingest route missing — where nothing is marked broken but nothing is draining either.
/// </remarks>
public sealed class VanSalesReceiptIngestHealthCheck(
    IServiceScopeFactory scopeFactory,
    IOptions<FiscalisationSettings> fiscalisationOptions) : IHealthCheck
{
    /// <summary>
    /// The drain runs every two minutes, so a receipt that has been waiting this long is not waiting on
    /// the schedule. Generous enough that a slow run or a brief platform blip does not raise an alert.
    /// </summary>
    private static readonly TimeSpan BacklogWarningAge = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Past this the day's file is at risk, not just late: a receipt still queued when its fiscal day
    /// closes is one ZIMRA never receives.
    /// </summary>
    private static readonly TimeSpan BacklogCriticalAge = TimeSpan.FromHours(2);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!fiscalisationOptions.Value.Enabled)
        {
            return HealthCheckResult.Healthy(
                "Fiscalisation is switched off, so no van receipts are queued for ZIMRA.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Mirrors the drain's own selection so the two agree on what "outstanding" means. Unstamped is
        // excluded for the same reason it is there: it consumed no receipt number, so nothing is
        // waiting behind it and it stops no device.
        var outstanding = dbContext.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.SourceSystem != null &&
                           SaleSourceSystems.VanSaleSources.Contains(sale.SourceSystem) &&
                           sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.NotApplicable &&
                           sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.Ingested &&
                           sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.Unstamped);

        var chainBroken = await outstanding
            .CountAsync(sale => sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.ChainBroken,
                cancellationToken);

        var unsignable = await outstanding
            .CountAsync(sale => sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Unsignable,
                cancellationToken);

        var exhausted = await outstanding
            .CountAsync(sale => sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Failed &&
                                sale.ReceiptIngestAttempts >= VanSalesSignedReceiptIngestService.MaxIngestAttempts,
                cancellationToken);

        var waiting = await outstanding.CountAsync(cancellationToken);

        var oldestCreatedAt = await outstanding
            .MinAsync(sale => (DateTime?)sale.CreatedAt, cancellationToken);

        // Counted per device rather than per receipt because the unit of damage is the handset: one
        // broken chain stops that van's day regardless of how many receipts are stacked behind it.
        var stoppedDevices = await outstanding
            .Where(sale => sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.ChainBroken ||
                           sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Unsignable ||
                           (sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Failed &&
                            sale.ReceiptIngestAttempts >= VanSalesSignedReceiptIngestService.MaxIngestAttempts))
            .Select(sale => sale.FiscalDeviceId)
            .Distinct()
            .CountAsync(cancellationToken);

        var oldestAge = oldestCreatedAt.HasValue
            ? DateTime.UtcNow - oldestCreatedAt.Value
            : TimeSpan.Zero;

        var data = new Dictionary<string, object>
        {
            ["chainBroken"] = chainBroken,
            ["unsignable"] = unsignable,
            ["retriesExhausted"] = exhausted,
            ["stoppedDevices"] = stoppedDevices,
            ["waiting"] = waiting,
            ["oldestWaitingMinutes"] = Math.Round(oldestAge.TotalMinutes, 0)
        };

        var blocked = chainBroken + unsignable + exhausted;
        if (blocked > 0)
        {
            var reasons = new List<string>(3);
            if (chainBroken > 0)
            {
                reasons.Add($"{chainBroken} chain break(s)");
            }

            if (unsignable > 0)
            {
                reasons.Add($"{unsignable} receipt(s) that can never be submitted");
            }

            if (exhausted > 0)
            {
                reasons.Add($"{exhausted} receipt(s) past {VanSalesSignedReceiptIngestService.MaxIngestAttempts} attempts");
            }

            return HealthCheckResult.Unhealthy(
                $"{stoppedDevices} van handset(s) cannot deliver receipts to ZIMRA: {string.Join(", ", reasons)}. " +
                "Their receipts are refused from that point on and their fiscal days cannot be packaged " +
                "until a person reconciles them. Do not resend.",
                data: data);
        }

        if (waiting > 0 && oldestAge >= BacklogCriticalAge)
        {
            return HealthCheckResult.Unhealthy(
                $"{waiting} signed van receipt(s) have not reached the fiscalisation platform, the oldest " +
                $"waiting {oldestAge.TotalHours:N1}h. Nothing is marked broken, so the platform is likely " +
                "unreachable or missing its ingest route. A receipt still queued when its fiscal day closes " +
                "never reaches ZIMRA.",
                data: data);
        }

        if (waiting > 0 && oldestAge >= BacklogWarningAge)
        {
            return HealthCheckResult.Degraded(
                $"{waiting} signed van receipt(s) are waiting for the fiscalisation platform, the oldest " +
                $"{oldestAge.TotalMinutes:N0}m. The drain runs every two minutes, so this is not the schedule.",
                data: data);
        }

        return HealthCheckResult.Healthy(
            waiting == 0
                ? "No signed van receipts are waiting for the fiscalisation platform."
                : $"{waiting} signed van receipt(s) are queued and draining normally.",
            data);
    }
}
