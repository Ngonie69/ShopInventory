using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.Invoices.Commands.FiscalizeInvoice;
using ShopInventory.Features.Invoices.Queries.GetInvoiceByDocNum;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Commands.RetryFailedInvoiceFiscalisations;

/// <summary>
/// Files again the SAP invoices this app posted whose fiscalisation failed.
/// </summary>
/// <remarks>
/// <para>
/// An invoice this app posts to SAP is fiscalised once, by <see cref="InvoiceFiscalizationBackgroundService"/>,
/// straight after it is created. When that attempt fails the invoice is recorded Failed and nothing tries it
/// again: on 2026-10-17 and -18, with ZIMRA unreachable, every invoice raised stayed owed until a person pressed
/// Fiscalise on the fiscalisation console. This is that button on a timer.
/// </para>
/// <para>
/// Each invoice goes through <see cref="FiscalizeInvoiceCommand"/>, the route the button uses, so every refusal
/// it makes (reposted after the SAP update, consolidated, already fiscalised as a till sale, posted before its
/// sale's receipt) holds here too and nothing is filed by a second set of rules. Sending one again is safe at
/// the platform, which keys the receipt on the DocNum and answers a second submission with the first receipt.
/// </para>
/// <para>
/// Never taken: an invoice with any evidence of a receipt; one whose outcome someone could not establish
/// (<see cref="FiscalOutcomeMessages.IsUnresolved"/>, on any of its rows: the next step there is a look-up by a
/// person); one whose latest row is not a failure from one of the fiscalise routes; one that first failed more
/// than <see cref="InvoiceFiscalisationRetrySettings.LookbackDays"/> ago; and one still waiting out its back-off.
/// </para>
/// <para>
/// A pass stops at the first invoice that fails. During an outage every invoice fails the same way, so the pass
/// then costs the device one request, and the back-off spaces each invoice's own attempts out to an hour.
/// </para>
/// </remarks>
public sealed class RetryFailedInvoiceFiscalisationsHandler(
    ApplicationDbContext db,
    ISender sender,
    InvoiceFiscalisationRetryHolds holds,
    IOptions<InvoiceFiscalisationRetrySettings> settings,
    IOptions<FiscalisationSettings> fiscalisationSettings,
    IOptions<RevmaxSettings> revmaxSettings,
    ILogger<RetryFailedInvoiceFiscalisationsHandler> logger)
    : IRequestHandler<RetryFailedInvoiceFiscalisationsCommand, ErrorOr<RetryFailedInvoiceFiscalisationsResult>>
{
    /// <summary>The fiscal transaction rows this pass writes.</summary>
    public const string SourceSystem = "InvoiceFiscalisationRetry";

    private const string InvoiceDocumentType = "Invoice";
    private const string Username = "invoice-fiscalisation-retry";

    /// <summary>The routes whose failures this pass owns. A row any other writer left is not its to act on.</summary>
    private static readonly string[] RetriedSources =
    [
        InvoiceFiscalizationBackgroundService.SourceSystem,
        FiscalizeInvoiceHandler.SourceSystem,
        SourceSystem
    ];

    public async Task<ErrorOr<RetryFailedInvoiceFiscalisationsResult>> Handle(
        RetryFailedInvoiceFiscalisationsCommand command,
        CancellationToken cancellationToken)
    {
        var result = new RetryFailedInvoiceFiscalisationsResult();

        // Under a switched-off provider every "result" is a skip that would be recorded as fiscalised.
        var providerEnabled = fiscalisationSettings.Value.UsesPlatform
            ? fiscalisationSettings.Value.Enabled
            : revmaxSettings.Value.Enabled;

        if (!settings.Value.Enabled || !providerEnabled)
        {
            return result;
        }

        var candidates = await FindCandidatesAsync(DateTime.UtcNow, cancellationToken);

        if (candidates.Count == 0)
        {
            return result;
        }

        logger.LogInformation(
            "Retrying fiscalisation of {Count} SAP invoices whose last attempt failed.", candidates.Count);

        foreach (var candidate in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var invoice = await sender.Send(new GetInvoiceByDocNumQuery(candidate.DocNum), cancellationToken);

            if (invoice.IsError)
            {
                if (invoice.FirstError.Type == ErrorType.NotFound)
                {
                    Hold(candidate.DocNum, result, invoice.FirstError.Description);
                    continue;
                }

                // SAP is what every other invoice in the pass needs too.
                result.StoppedBecause = $"SAP could not be read: {invoice.FirstError.Description}";
                break;
            }

            var attempt = await sender.Send(
                new FiscalizeInvoiceCommand(invoice.Value.DocEntry, null, Username, SourceSystem),
                cancellationToken);

            if (attempt.IsError)
            {
                if (attempt.FirstError.Type is ErrorType.Conflict or ErrorType.NotFound or ErrorType.Validation)
                {
                    Hold(candidate.DocNum, result, attempt.FirstError.Description);
                    continue;
                }

                result.StoppedBecause = $"SAP could not be read: {attempt.FirstError.Description}";
                break;
            }

            var filed = attempt.Value;

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

            result.Failed++;
            result.StoppedBecause = filed switch
            {
                { RequiresReconciliation: true } =>
                    $"Invoice {candidate.DocNum} needs reconciling before anything else is filed.",
                { ErrorCode: RevmaxHistoryFiscalizationService.HistoryUnavailableErrorCode } =>
                    "REVMax could not be asked what it already holds.",
                { Skipped: true } =>
                    filed.Message ?? "The fiscal service skipped the submission.",
                _ =>
                    $"Invoice {candidate.DocNum} failed again ({filed.ErrorCode ?? "no code"}): {filed.Message}"
            };
            break;
        }

        logger.LogInformation(
            "Invoice fiscalisation retry finished: {Fiscalised} fiscalised, {AlreadyHeld} already held a receipt, "
            + "{Refused} refused, {Failed} failed.{Stopped}",
            result.Fiscalised,
            result.AlreadyHeld,
            result.Refused,
            result.Failed,
            result.StoppedBecause is null ? string.Empty : $" Stopped early: {result.StoppedBecause}");

        return result;
    }

    /// <summary>
    /// The invoices this pass would take at <paramref name="nowUtc"/>, the earliest failure first, at most a batch.
    /// </summary>
    internal async Task<List<InvoiceFiscalisationRetryCandidate>> FindCandidatesAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var options = settings.Value;
        var cutoff = nowUtc.AddDays(-Math.Max(1, options.LookbackDays));

        var docNums = await db.DesktopFiscalTransactions
            .AsNoTracking()
            .Where(transaction => transaction.DocumentType == InvoiceDocumentType
                                  && transaction.DocNum > 0
                                  && transaction.Status == FiscalTransactionStatus.Failed
                                  && transaction.TimestampUtc >= cutoff
                                  && RetriedSources.Contains(transaction.SourceSystem))
            .Select(transaction => transaction.DocNum)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (docNums.Count == 0)
        {
            return [];
        }

        // Every row for those invoices, however old: a receipt recorded before the window is still a receipt.
        var history = (await db.DesktopFiscalTransactions
                .AsNoTracking()
                .Where(transaction => transaction.DocumentType == InvoiceDocumentType && docNums.Contains(transaction.DocNum))
                .ToListAsync(cancellationToken))
            .ToLookup(transaction => transaction.DocNum);

        // The invoices the fiscalise route would refuse as filed under a sale, asked here in one query each so
        // they never reach it. The route still asks again; these only save the SAP reads.
        var consolidated = await ConsolidatedInvoiceRegistry.FindConsolidatedDocNumsAsync(db, docNums, cancellationToken);
        var perSale = await PerSaleInvoiceRegistry.FindPerSaleDocNumsAsync(db, docNums, cancellationToken);
        var receiptPending = await PerSaleInvoiceRegistry.FindReceiptPendingSalesAsync(db, docNums, cancellationToken);

        var candidates = new List<InvoiceFiscalisationRetryCandidate>();

        foreach (var docNum in docNums)
        {
            var rows = history[docNum].ToList();
            var latest = rows
                .OrderByDescending(row => row.LastSyncedAtUtc)
                .ThenByDescending(row => row.Id)
                .First();

            if (rows.Any(FiscalDocumentStatusProjector.HasFiscalEvidencePredicate)
                || rows.Any(row => FiscalOutcomeMessages.IsUnresolved(row.Message))
                || latest.RepostedAfterSapUpdate
                || latest.Status != FiscalTransactionStatus.Failed
                || !RetriedSources.Contains(latest.SourceSystem)
                || consolidated.Contains(docNum)
                || perSale.Contains(docNum)
                || receiptPending.ContainsKey(docNum)
                || holds.IsHeld(docNum, nowUtc))
            {
                continue;
            }

            var failures = rows.Where(row => row.Status == FiscalTransactionStatus.Failed).ToList();
            var firstFailedAtUtc = failures.Min(row => row.TimestampUtc);
            var lastFailedAtUtc = failures.Max(row => row.TimestampUtc);

            if (firstFailedAtUtc < cutoff || lastFailedAtUtc + options.RetryDelay(failures.Count) > nowUtc)
            {
                continue;
            }

            candidates.Add(new InvoiceFiscalisationRetryCandidate(docNum, failures.Count, firstFailedAtUtc));
        }

        return candidates
            .OrderBy(candidate => candidate.FirstFailedAtUtc)
            .ThenBy(candidate => candidate.DocNum)
            .Take(Math.Max(1, options.BatchSize))
            .ToList();
    }

    private void Hold(int docNum, RetryFailedInvoiceFiscalisationsResult result, string reason)
    {
        result.Refused++;
        holds.Hold(docNum, DateTime.UtcNow.AddMinutes(Math.Max(1, settings.Value.MaxRetryMinutes)));

        logger.LogWarning(
            "Not retrying fiscalisation of invoice {DocNum} for now: {Reason}", docNum, reason);
    }
}
