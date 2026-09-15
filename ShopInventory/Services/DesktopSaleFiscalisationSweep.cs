using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Fiscalises the sales that were left to be fiscalised later, and retries the ones that failed.
///
/// Vending raises an invoice against a cart vendor and prints nothing, so there is no receipt for
/// anyone to wait on and no reason to hold the request open while the platform signs it. The sale is
/// stored <see cref="DesktopSaleFiscalizationStatus.Pending"/> and this picks it up moments later. A
/// shop till sale fiscalises in its own request, and this is what recovers it when that attempt fails.
///
/// It is not optional. Nothing else moves a sale out of Pending or Failed, and
/// <see cref="DesktopSalePostingService"/> only posts sales that have fiscalised — so without this a
/// sale would sit fiscalised-by-nobody and invoiced-by-nobody, indefinitely and silently.
/// </summary>
public sealed class DesktopSaleFiscalisationSweep(
    ApplicationDbContext context,
    DesktopSaleFiscaliser fiscaliser,
    IOptions<DesktopSalePostingSettings> settings,
    IOptions<FiscalisationSettings> fiscalisationSettings,
    ILogger<DesktopSaleFiscalisationSweep> logger)
{
    public async Task<DesktopSaleFiscalisationRunResult> FiscalisePendingSalesAsync(
        CancellationToken cancellationToken = default)
    {
        var options = settings.Value;
        var result = new DesktopSaleFiscalisationRunResult();
        var cutoff = DateTime.UtcNow.Date.AddDays(-options.LookbackDays);

        // Which sources this sweep owns depends on who signs a van's receipts — see
        // DesktopSaleFiscalisationRetry.RetriedSources, which the retry command and the console read too.
        var sweptSources = DesktopSaleFiscalisationRetry.RetriedSources(fiscalisationSettings.Value.UsesPlatform);

        // A shop till sale fiscalises inline and was once excluded outright, so one failed attempt at the
        // counter — the device busy, the network down for a moment — left it Failed for good: never
        // retried, and never invoiced, because the posting job takes only fiscalised sales. It is taken
        // now, but not while its own request may still be fiscalising it: the till commits the sale
        // Pending and holds it Pending for the whole round trip.
        var inlineCutoff = DateTime.UtcNow - DesktopSaleFiscalisationRetry.InlineRequestWindow;

        var pending = await context.DesktopSales
            .Include(s => s.Lines)
            .Where(s => s.DocDate >= cutoff &&
                        sweptSources.Contains(s.SourceSystem) &&
                        // Failed as well as Pending. A failure sets Failed, so selecting on Pending
                        // alone made a single transient error terminal — the sale was never retried,
                        // never invoiced, and the attempt budget below was unreachable.
                        (s.FiscalizationStatus == DesktopSaleFiscalizationStatus.Failed ||
                         (s.FiscalizationStatus == DesktopSaleFiscalizationStatus.Pending &&
                          (s.SourceSystem != SaleSourceSystems.ShopTill || s.CreatedAt < inlineCutoff))) &&
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

            // Whether an earlier attempt may already have reached the platform. Read before the call,
            // because FiscaliseAsync increments it.
            var isRetry = DesktopSaleFiscalisationRetry.MayAlreadyBeFiled(sale);

            try
            {
                await fiscaliser.FiscaliseAsync(sale, cancellationToken, isRetry);
            }
            catch (Exception ex)
            {
                // Only the pre-submission lookup throws — FiscaliseAsync swallows everything from the
                // submission itself. So this means we could not establish whether a receipt already
                // exists, and submitting blind is the one thing that must not happen. Leave the sale
                // exactly as it was for the next pass.
                logger.LogWarning(
                    ex,
                    "Skipped fiscalising sale {ExternalReference}: could not establish whether it has "
                    + "already been signed.",
                    sale.ExternalReferenceId);

                context.Entry(sale).State = EntityState.Unchanged;
                result.Failed++;
                continue;
            }

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
