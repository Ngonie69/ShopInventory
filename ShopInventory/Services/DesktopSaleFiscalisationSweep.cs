using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Fiscalises the sales that were left to be fiscalised later.
///
/// Vending raises an invoice against a cart vendor and prints nothing, so there is no receipt for
/// anyone to wait on and no reason to hold the request open while the platform signs it. The sale is
/// stored <see cref="DesktopSaleFiscalizationStatus.Pending"/> and this picks it up moments later.
///
/// It is not optional. Nothing else moves a sale out of Pending, and
/// <see cref="DesktopSalePostingService"/> only posts sales that have fiscalised — so without this a
/// vending sale would sit fiscalised-by-nobody and invoiced-by-nobody, indefinitely and silently.
/// </summary>
public sealed class DesktopSaleFiscalisationSweep(
    ApplicationDbContext context,
    DesktopSaleFiscaliser fiscaliser,
    IOptions<DesktopSalePostingSettings> settings,
    ILogger<DesktopSaleFiscalisationSweep> logger)
{
    public async Task<DesktopSaleFiscalisationRunResult> FiscalisePendingSalesAsync(
        CancellationToken cancellationToken = default)
    {
        var options = settings.Value;
        var result = new DesktopSaleFiscalisationRunResult();
        var cutoff = DateTime.UtcNow.Date.AddDays(-options.LookbackDays);

        var pending = await context.DesktopSales
            .Include(s => s.Lines)
            .Where(s => s.DocDate >= cutoff &&
                        s.SourceSystem != null &&
                        SaleSourceSystems.PostedByDesktopSaleJob.Contains(s.SourceSystem) &&
                        s.FiscalizationStatus == DesktopSaleFiscalizationStatus.Pending &&
                        // Never retried: the receipt may already exist at FDMS and a second
                        // submission cannot be withdrawn.
                        !s.FiscalizationRequiresReconciliation &&
                        s.FiscalizationAttempts < options.MaxFiscalisationAttempts)
            .OrderBy(s => s.Id)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return result;
        }

        logger.LogInformation("Fiscalising {Count} sales left pending.", pending.Count);

        foreach (var sale in pending)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Fiscalisation sweep was cancelled after {Done} of {Total}.", result.Total, pending.Count);
                break;
            }

            await fiscaliser.FiscaliseAsync(sale, cancellationToken);

            switch (sale.FiscalizationStatus)
            {
                case DesktopSaleFiscalizationStatus.Success:
                    result.Fiscalised++;
                    break;
                case DesktopSaleFiscalizationStatus.Skipped:
                    result.Skipped++;
                    break;
                default:
                    result.Failed++;
                    break;
            }

            // Saved per sale. The receipt may have been signed at FDMS by the time this returns, and
            // losing that to a crash later in the batch would leave the sale looking unfiscalised —
            // which is the state that gets a receipt signed a second time.
            await context.SaveChangesAsync(CancellationToken.None);
        }

        logger.LogInformation(
            "Fiscalisation sweep finished: {Fiscalised} fiscalised, {Skipped} skipped, {Failed} failed.",
            result.Fiscalised, result.Skipped, result.Failed);

        return result;
    }
}

public sealed class DesktopSaleFiscalisationRunResult
{
    public int Fiscalised { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }

    public int Total => Fiscalised + Skipped + Failed;
}
