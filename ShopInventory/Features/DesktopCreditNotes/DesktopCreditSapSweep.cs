using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DesktopCreditNotes;

/// <summary>
/// Finds the fiscalised credits that SAP is still owed, and raises them.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of work, and the first is not a failure. A credit taken at the counter before its sale
/// posts is <see cref="DesktopCreditSapStatuses.Deferred"/> by design and becomes raisable the moment
/// the sale reaches SAP; <c>DesktopSalePostingService</c> raises it there and then, so in the ordinary
/// case this pass finds nothing. It exists for what that hook cannot cover — a sale adopted rather
/// than posted, a process that died between the two — and for retrying the second kind, a memo SAP
/// refused or could not be asked about.
/// </para>
/// <para>
/// Without it a credit whose SAP half failed once would stay failed for good, and ZIMRA and SAP would
/// disagree about a return permanently, with nothing but a row to say so.
/// </para>
/// </remarks>
public sealed class DesktopCreditSapSweep(
    ApplicationDbContext db,
    DesktopCreditSapPoster poster,
    IOptions<DesktopSalePostingSettings> settings,
    ILogger<DesktopCreditSapSweep> logger)
{
    public async Task<DesktopCreditSapRunResult> SettleOutstandingAsync(
        CancellationToken cancellationToken = default)
    {
        var options = settings.Value;
        var result = new DesktopCreditSapRunResult();
        var cutoff = DateTime.UtcNow.Date.AddDays(-options.LookbackDays);

        var outstanding = await db.DesktopCreditNotes
            .AsNoTracking()
            .Where(note => note.CreatedAtUtc >= cutoff
                // ZIMRA first, always. A credit the device refused, or one whose outcome nobody has
                // established, must never become a SAP document.
                && note.Status == DesktopCreditStatuses.Fiscalised
                && (
                    // Owed and now raisable: Deferred means the sale had not posted, so this asks
                    // whether it has. Nothing was sent, so no attempt is spent and no cap applies.
                    (note.SapStatus == DesktopCreditSapStatuses.Deferred && note.Sale.SapDocEntry != null)
                    // A memo SAP refused. The cap rations these, as it does a sale's own posting.
                    || (note.SapStatus == DesktopCreditSapStatuses.Failed
                        && note.SapAttempts < options.MaxPostingAttempts)
                    // Fiscalised but never carried forward at all — the units are still owed to the
                    // ledger even where SAP turns out to be owed nothing.
                    || !note.UnitsReturnedToLedger))
            .OrderBy(note => note.CreatedAtUtc)
            .Take(options.BatchSize)
            .Select(note => note.Id)
            .ToListAsync(cancellationToken);

        if (outstanding.Count == 0)
        {
            return result;
        }

        logger.LogInformation("Settling {Count} fiscal credits that SAP is still owed.", outstanding.Count);

        foreach (var id in outstanding)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Credit posting sweep was cancelled after {Done} of {Total}.", result.Total, outstanding.Count);
                break;
            }

            try
            {
                // One at a time, and the poster saves as it goes: a memo may be in SAP by the time this
                // returns, and losing that to a crash later in the batch is how a second one gets raised.
                await poster.SettleAsync(id, cancellationToken);
                result.Settled++;
            }
            catch (Exception ex)
            {
                // The poster records its own failures on the row; reaching here means something outside
                // them threw. The next pass is the right retry.
                logger.LogError(ex, "Settling credit {CreditNoteId} threw.", id);
                result.Failed++;
            }
        }

        logger.LogInformation(
            "Credit posting sweep finished: {Settled} settled, {Failed} failed.", result.Settled, result.Failed);

        return result;
    }
}

public sealed class DesktopCreditSapRunResult
{
    /// <summary>
    /// Credits this pass carried forward. Not a count of documents raised: a credit whose sale has
    /// still not posted is carried as far as it can go, which today is nowhere.
    /// </summary>
    public int Settled { get; set; }

    public int Failed { get; set; }

    public int Total => Settled + Failed;
}
