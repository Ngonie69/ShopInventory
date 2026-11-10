using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.CreditNotes;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Fiscalises the SAP credit memos that nothing else has filed with ZIMRA.
/// </summary>
/// <remarks>
/// <para>
/// A credit memo reaches ZIMRA by one of three routes, and before this the most common one had no owner.
/// A memo this app raises is fiscalised in the request that raises it. A memo keyed straight into SAP B1
/// is fiscalised by the platform's B1 bridge only if a clerk prints it — the bridge hooks the print, and
/// nothing polls SAP. Before the 2026-09-30 cut-over REVMax's own SAP add-on filed every memo; after it,
/// an unprinted memo filed nowhere, and the van credit-note list showed a column of "No receipt".
/// </para>
/// <para>
/// So this reads the credit memo projection, keeps the memos the fiscal transaction log holds no evidence
/// for, and files each through <see cref="SapCreditNoteFiscaliser"/> — the same routine the approval add
/// uses, so the receipt a credit reverses is chosen in one place. Filing is safe against every other
/// route: the platform refuses a second receipt for one number (and this adopts the first), and REVMax is
/// asked what it holds before anything is filed there. A memo already filed that way therefore costs one
/// lookup, and leaves a fiscal transaction row that makes the lists show its receipt.
/// </para>
/// <para>
/// Never taken: a cancelled memo; one a till credit filed before SAP (<see cref="PerSaleCreditNoteRegistry"/>
/// — filing it again would hand the money back twice); one whose last outcome nobody could establish
/// (<see cref="FiscalOutcomeMessages.IsUnresolved"/> — the next step there is a lookup by a person); and
/// one that has used its attempts.
/// </para>
/// </remarks>
public sealed class SapCreditNoteFiscalisationSweep(
    ApplicationDbContext db,
    ISAPServiceLayerClient sap,
    SapCreditNoteFiscaliser fiscaliser,
    IOptions<CreditNoteFiscalisationSettings> settings,
    IOptions<FiscalisationSettings> fiscalisationSettings,
    IOptions<RevmaxSettings> revmaxSettings,
    ILogger<SapCreditNoteFiscalisationSweep> logger)
{
    /// <summary>The fiscal transaction rows this pass writes, which is also how it counts its attempts.</summary>
    public const string SourceSystem = "CreditNoteSweep";

    private const string CreditNoteDocumentType = "CreditNote";

    /// <summary>How many batches' worth of memos a pass reads, so that memos held for REVMax cannot fill it.</summary>
    private const int HeldBackHeadroom = 4;

    public async Task<SapCreditNoteFiscalisationRunResult> FiscaliseOutstandingAsync(CancellationToken cancellationToken)
    {
        var result = new SapCreditNoteFiscalisationRunResult();

        // Under a switched-off provider every "result" is a skip that would be recorded as fiscalised.
        var providerEnabled = fiscalisationSettings.Value.UsesPlatform
            ? fiscalisationSettings.Value.Enabled
            : revmaxSettings.Value.Enabled;

        if (!providerEnabled)
        {
            return result;
        }

        var options = settings.Value;
        var batchSize = Math.Max(1, options.BatchSize);

        // Memos held back for REVMax take no part of the batch, so more are read than one batch: enough
        // that a run of them cannot fill it and starve every newer memo.
        var candidates = await FindCandidatesAsync(DateTime.UtcNow, cancellationToken, batchSize * HeldBackHeadroom);

        if (candidates.Count == 0)
        {
            return result;
        }

        logger.LogInformation("Fiscalising up to {Batch} of {Count} SAP credit memos that hold no fiscal receipt.", batchSize, candidates.Count);

        var answered = 0;

        foreach (var candidate in candidates)
        {
            if (cancellationToken.IsCancellationRequested || answered >= batchSize)
            {
                break;
            }

            SAPCreditNote? memo;

            try
            {
                memo = await sap.GetCreditNoteByDocEntryAsync(candidate.DocEntry, cancellationToken);
            }
            catch (Exception readFailure) when (readFailure is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // SAP is what every other memo in the pass needs too.
                logger.LogWarning(
                    readFailure, "Could not read credit memo {DocNum} from SAP; leaving the rest for the next pass.", candidate.DocNum);
                result.StoppedBecause = "SAP could not be read.";
                break;
            }

            // Cancelled since the projection last saw it, or gone: nothing is owed.
            if (memo is null || string.Equals(memo.Cancelled, "tYES", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var attempt = candidate.PriorAttempts + 1;

            // Counted as the batch's before the outcome is known; a memo held back for REVMax hands its
            // place back below.
            answered++;

            var outcome = await fiscaliser.FiscaliseAsync(
                memo,
                new SapCreditNoteFiscalisationCaller(
                    SourceSystem,
                    "credit-note-sweep",
                    $"scheduled sweep, attempt {attempt} of {options.MaxAttempts}",
                    CaptureIncidents: attempt >= options.MaxAttempts),
                cancellationToken);

            if (outcome.Refusal is not null)
            {
                result.Refused++;
                continue;
            }

            if (outcome.Exception is { } exception)
            {
                result.Failed++;

                if (RevmaxReachability.IsNoAnswer(exception))
                {
                    result.StoppedBecause = "The fiscal device did not answer.";
                    break;
                }

                continue;
            }

            var filed = outcome.Result!;

            if (filed.Success)
            {
                if (filed.Skipped || filed.AlreadyFiscalised)
                {
                    result.AlreadyHeld++;
                }
                else
                {
                    result.Fiscalised++;
                }

                continue;
            }

            // REVMax could not be asked whether it filed this memo's original invoice. That says nothing
            // about the memos after it: a memo whose original is on the platform never asks REVMax. So
            // this one is passed over and the pass goes on. Nothing was recorded, so the next pass asks
            // again. Each ask costs little while REVMax is down: RevmaxReachability refuses reads for a
            // minute after one goes unanswered.
            if (filed.ErrorCode == RevmaxHistoryFiscalizationService.HistoryUnavailableErrorCode)
            {
                answered--;
                result.HeldForRevmax.Add(candidate.DocNum);
                continue;
            }

            result.Failed++;

            // Each of these says the same about every memo after it, and none of them was recorded as an
            // attempt: the platform is in a dry run, or nobody knows what the last submission did — which
            // is no moment to send another.
            if (filed.Skipped)
            {
                result.StoppedBecause = filed.Message ?? "The fiscal service skipped the submission.";
                break;
            }

            if (filed.RequiresReconciliation)
            {
                result.StoppedBecause = $"Credit memo {candidate.DocNum} needs reconciling before anything else is filed.";
                break;
            }
        }

        if (result.HeldForRevmax.Count > 0)
        {
            logger.LogWarning(
                "Credit memos held back because REVMax could not be asked whether it filed their original invoice: {DocNums}. "
                + "Only REVMax can credit its own receipts; they are tried again next pass.",
                string.Join(", ", result.HeldForRevmax));
        }

        logger.LogInformation(
            "Credit memo fiscalisation finished: {Fiscalised} fiscalised, {AlreadyHeld} already held a receipt, "
            + "{Refused} refused, {Failed} failed, {HeldForRevmax} held for REVMax.{Stopped}",
            result.Fiscalised,
            result.AlreadyHeld,
            result.Refused,
            result.Failed,
            result.HeldForRevmax.Count,
            result.StoppedBecause is null ? string.Empty : $" Stopped early: {result.StoppedBecause}");

        return result;
    }

    /// <summary>
    /// The memos this pass would take at <paramref name="nowUtc"/>, oldest first, at most a batch.
    /// </summary>
    internal async Task<List<SapCreditNoteFiscalisationCandidate>> FindCandidatesAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken,
        int? limit = null)
    {
        var options = settings.Value;
        var take = Math.Max(1, limit ?? options.BatchSize);
        var from = AuditService.ToCAT(nowUtc).Date.AddDays(-(Math.Max(1, options.LookbackDays) - 1));

        if (options.NotBefore is { } notBefore && notBefore.Date > from)
        {
            from = notBefore.Date;
        }
        var settledBefore = nowUtc.AddMinutes(-Math.Max(0, options.GraceMinutes));
        var excluded = options.ExcludedPrefixes();

        var memos = (await db.SapCreditNoteSnapshots
                .AsNoTracking()
                .Where(memo => !memo.IsCancelled
                               && memo.DocDate >= from
                               && memo.SyncedAtUtc <= settledBefore
                               && memo.SapDocNum > 0)
                .OrderBy(memo => memo.DocDate)
                .ThenBy(memo => memo.SapDocNum)
                .Select(memo => new { memo.SapDocEntry, memo.SapDocNum, memo.CardCode })
                .ToListAsync(cancellationToken))
            .Where(memo => !excluded.Any(prefix =>
                memo.CardCode?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true))
            .ToList();

        if (memos.Count == 0)
        {
            return [];
        }

        var docNums = memos.Select(memo => memo.SapDocNum).Distinct().ToList();

        var transactions = await db.DesktopFiscalTransactions
            .AsNoTracking()
            .Where(transaction => transaction.DocumentType == CreditNoteDocumentType && docNums.Contains(transaction.DocNum))
            .ToListAsync(cancellationToken);

        var history = transactions.ToLookup(transaction => transaction.DocNum);

        var filedBeforeSap = await PerSaleCreditNoteRegistry.FindPerSaleDocNumsAsync(db, docNums, cancellationToken);

        var candidates = new List<SapCreditNoteFiscalisationCandidate>();

        foreach (var memo in memos)
        {
            var rows = history[memo.SapDocNum].ToList();

            if (rows.Any(FiscalDocumentStatusProjector.HasFiscalEvidencePredicate)
                || filedBeforeSap.Contains(memo.SapDocNum)
                || rows.Any(row => FiscalOutcomeMessages.IsUnresolved(row.Message)))
            {
                continue;
            }

            var attempts = rows.Count(row => row.SourceSystem == SourceSystem);

            if (attempts >= options.MaxAttempts)
            {
                continue;
            }

            candidates.Add(new SapCreditNoteFiscalisationCandidate(memo.SapDocEntry, memo.SapDocNum, attempts));

            if (candidates.Count >= take)
            {
                break;
            }
        }

        return candidates;
    }
}

public sealed record SapCreditNoteFiscalisationCandidate(int DocEntry, int DocNum, int PriorAttempts);

public sealed class SapCreditNoteFiscalisationRunResult
{
    public int Fiscalised { get; set; }

    /// <summary>A device already held the receipt — printed in B1, or filed by REVMax — and it was adopted.</summary>
    public int AlreadyHeld { get; set; }

    /// <summary>No receipt to reverse could be named, so nothing was sent.</summary>
    public int Refused { get; set; }

    public int Failed { get; set; }

    /// <summary>
    /// Memos passed over because REVMax could not be asked whether it filed their original invoice.
    /// Nothing was recorded for them, so the next pass takes them again.
    /// </summary>
    public List<int> HeldForRevmax { get; } = [];

    /// <summary>Why the pass ended before its batch did, when it did.</summary>
    public string? StoppedBecause { get; set; }
}
