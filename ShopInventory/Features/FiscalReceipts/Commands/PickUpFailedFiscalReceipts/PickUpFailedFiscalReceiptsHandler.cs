using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Services;

namespace ShopInventory.Features.FiscalReceipts.Commands.PickUpFailedFiscalReceipts;

/// <summary>
/// Reads back from the fiscal device the receipts of SAP invoices and credit notes whose fiscalisation failed, and
/// records the ones it finds against the document.
/// </summary>
/// <remarks>
/// <para>
/// A failed attempt is not always a missing receipt. The platform can sign a document after the call that asked
/// for it has timed out or been refused, and someone can fiscalise it on the platform's own console. Until a person
/// pressed Fiscalise here, and the platform answered that press with the receipt it already held, the document read
/// "Not fiscalised" on every list and its PDF printed no fiscal block. This is that look-up on a timer.
/// </para>
/// <para>
/// It asks and never files. That is why it takes what the passes that do file must not: for invoices, a person's
/// failed press and an invoice raised in SAP (<c>RetryFailedInvoiceFiscalisationsHandler</c>); for both kinds, a
/// document whose outcome nobody established (<see cref="FiscalOutcomeMessages.IsUnresolved"/>), whose next step is
/// exactly this look-up; and a credit note the sweep has stopped sending (<see cref="SapCreditNoteFiscalisationSweep"/>
/// — out of attempts, or refused). Sending any of those again could sign a document twice; reading one cannot.
/// </para>
/// <para>
/// A receipt found is recorded as a <c>Fiscalised</c> row, which every reader of the log treats as evidence
/// (<see cref="FiscalDocumentStatusProjector.HasFiscalEvidenceExpression"/>): the invoice and credit note lists, their
/// PDFs, the console's work queue, and the passes that file, which then leave the document alone. A "not there yet"
/// records nothing; <see cref="FiscalReceiptPickupSchedule"/> spaces the next look-up out instead.
/// </para>
/// <para>
/// Only a receipt dated no earlier than the document's first row, less
/// <see cref="FiscalReceiptPickupSettings.ReceiptDateSlackHours"/>, is taken. SAP reissued document numbers after its
/// September 2026 update, and a look-up is by number alone, so an older receipt under the same number is an earlier
/// document's — for a credit note, possibly another customer's. A REVMax receipt whose date cannot be read is dated
/// now by its reader, and so passes; the platform always dates its receipts.
/// </para>
/// <para>
/// Never asked about: a document with any evidence of a receipt already; one whose latest row is not a failed attempt;
/// one reposted after the SAP update; one recorded under a till sale or a consolidation, whose receipt is filed under
/// another number; and one whose latest failure is more than <see cref="FiscalReceiptPickupSettings.LookbackDays"/> old.
/// </para>
/// <para>
/// A pass stops at the first look-up that cannot be answered. When the device is unreachable every look-up fails
/// the same way, so the pass then costs it one request.
/// </para>
/// </remarks>
public sealed class PickUpFailedFiscalReceiptsHandler(
    ApplicationDbContext db,
    ISender sender,
    IFiscalReceiptReader reader,
    TimeProvider clock,
    FiscalReceiptPickupSchedule schedule,
    IOptions<FiscalReceiptPickupSettings> settings,
    IOptions<FiscalisationSettings> fiscalisationSettings,
    IOptions<RevmaxSettings> revmaxSettings,
    ILogger<PickUpFailedFiscalReceiptsHandler> logger)
    : IRequestHandler<PickUpFailedFiscalReceiptsCommand, ErrorOr<PickUpFailedFiscalReceiptsResult>>
{
    /// <summary>The fiscal transaction rows this pass writes.</summary>
    public const string SourceSystem = "FiscalReceiptPickup";

    private const string Username = "fiscal-receipt-pickup";

    public async Task<ErrorOr<PickUpFailedFiscalReceiptsResult>> Handle(
        PickUpFailedFiscalReceiptsCommand command,
        CancellationToken cancellationToken)
    {
        var result = new PickUpFailedFiscalReceiptsResult();

        // Under a switched-off provider there is no device to ask.
        var providerEnabled = fiscalisationSettings.Value.UsesPlatform
            ? fiscalisationSettings.Value.Enabled
            : revmaxSettings.Value.Enabled;

        if (!settings.Value.Enabled || !providerEnabled)
        {
            return result;
        }

        foreach (var document in Documents())
        {
            var candidates = await FindCandidatesAsync(document, Now(), cancellationToken);

            foreach (var candidate in candidates)
            {
                if (cancellationToken.IsCancellationRequested || !await PickUpAsync(candidate, result, cancellationToken))
                {
                    break;
                }
            }

            if (cancellationToken.IsCancellationRequested || result.StoppedBecause is not null)
            {
                break;
            }
        }

        if (result.Total > 0 || result.StoppedBecause is not null)
        {
            logger.LogInformation(
                "Fiscal receipt pick-up finished: {PickedUp} picked up, {NotYet} not there yet, {OlderReceipt} only an "
                + "older document's receipt, {NotRecorded} not recorded.{Stopped}",
                result.PickedUp,
                result.NotYet,
                result.OlderReceipt,
                result.NotRecorded,
                result.StoppedBecause is null ? string.Empty : $" Stopped early: {result.StoppedBecause}");
        }

        return result;
    }

    /// <summary>The kinds this pass asks about, invoices first.</summary>
    internal IEnumerable<FiscalReceiptPickupDocument> Documents()
    {
        yield return FiscalReceiptPickupDocument.Invoice;

        if (settings.Value.IncludeCreditNotes)
        {
            yield return FiscalReceiptPickupDocument.CreditNote;
        }
    }

    /// <returns>False when the device could not be asked, and the pass should stop.</returns>
    private async Task<bool> PickUpAsync(
        FiscalReceiptPickupCandidate candidate,
        PickUpFailedFiscalReceiptsResult result,
        CancellationToken cancellationToken)
    {
        var document = candidate.Document;
        FiscalReceiptSnapshot? snapshot;

        try
        {
            snapshot = await reader.TryLookupAsync(candidate.DocNum, document.ReceiptType, logger, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fiscal receipt look-up failed for {Document} {DocNum}", document.Label, candidate.DocNum);
            snapshot = null;
        }

        if (snapshot is null)
        {
            // "Could not find out" is not "not fiscalised", and it is what every other document would hear too.
            result.StoppedBecause =
                $"The fiscal device could not say whether it holds {document.Label} {candidate.DocNum}.";
            return false;
        }

        if (!snapshot.IsFiscalised)
        {
            result.NotYet++;
            RecordMiss(candidate);
            return true;
        }

        var earliestOwnReceiptUtc = candidate.FirstRecordedAtUtc.AddHours(-Math.Max(0, settings.Value.ReceiptDateSlackHours));

        if (snapshot.TimestampUtc < earliestOwnReceiptUtc)
        {
            result.OlderReceipt++;
            RecordMiss(candidate);
            logger.LogWarning(
                "The fiscal device holds receipt {ReceiptGlobalNo} under {Document} number {DocNum}, dated {ReceiptAtUtc:u}, "
                + "before this {Document} was first recorded ({FirstRecordedAtUtc:u}). It belongs to an earlier document "
                + "with the same number and was not taken.",
                snapshot.ReceiptGlobalNo,
                document.Label,
                candidate.DocNum,
                snapshot.TimestampUtc,
                document.Label,
                candidate.FirstRecordedAtUtc);
            return true;
        }

        var recorded = await sender.Send(
            new SyncFiscalTransactionCommand(BuildSyncRequest(candidate, snapshot), null, Username),
            cancellationToken);

        if (recorded.IsError)
        {
            // Asked again after the back-off; the receipt is still there to find.
            result.NotRecorded++;
            RecordMiss(candidate);
            logger.LogWarning(
                "The fiscal device holds receipt {ReceiptGlobalNo} for {Document} {DocNum}, but it could not be recorded: {Errors}",
                snapshot.ReceiptGlobalNo,
                document.Label,
                candidate.DocNum,
                string.Join("; ", recorded.Errors.Select(error => error.Description)));
            return true;
        }

        result.PickedUp++;
        schedule.Forget(document.DocumentType, candidate.DocNum);
        logger.LogInformation(
            "Picked up fiscal receipt {ReceiptGlobalNo} for {Document} {DocNum}, which the log last recorded as {Status} ({Source}).",
            snapshot.ReceiptGlobalNo,
            document.Label,
            candidate.DocNum,
            candidate.LatestFailure.Status,
            candidate.LatestFailure.SourceSystem);
        return true;
    }

    /// <summary>
    /// The documents of one kind this pass would ask about at <paramref name="nowUtc"/>, the earliest failure first,
    /// at most a batch.
    /// </summary>
    internal async Task<List<FiscalReceiptPickupCandidate>> FindCandidatesAsync(
        FiscalReceiptPickupDocument document,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var options = settings.Value;
        var cutoff = nowUtc.AddDays(-Math.Max(1, options.LookbackDays));
        var documentType = document.DocumentType;
        var owing = document.OwingStatuses.ToArray();
        var notBeforeUtc = NotBeforeUtc(document);

        schedule.Prune(cutoff);

        var docNums = await db.DesktopFiscalTransactions
            .AsNoTracking()
            .Where(transaction => transaction.DocumentType == documentType
                                  && transaction.DocNum > 0
                                  && owing.Contains(transaction.Status)
                                  && transaction.TimestampUtc >= cutoff)
            .Select(transaction => transaction.DocNum)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (docNums.Count == 0)
        {
            return [];
        }

        // Every row for those documents, however old: a receipt recorded before the window is still a receipt.
        var history = (await db.DesktopFiscalTransactions
                .AsNoTracking()
                .Where(transaction => transaction.DocumentType == documentType && docNums.Contains(transaction.DocNum))
                .ToListAsync(cancellationToken))
            .ToLookup(transaction => transaction.DocNum);

        var filedUnderAnotherNumber = await FindFiledUnderAnotherNumberAsync(document, docNums, cancellationToken);

        var candidates = new List<FiscalReceiptPickupCandidate>();

        foreach (var docNum in docNums)
        {
            var rows = history[docNum].ToList();
            var latest = rows
                .OrderByDescending(row => row.LastSyncedAtUtc)
                .ThenByDescending(row => row.Id)
                .First();

            if (rows.Any(FiscalDocumentStatusProjector.HasFiscalEvidencePredicate)
                || !owing.Contains(latest.Status)
                || latest.RepostedAfterSapUpdate
                || filedUnderAnotherNumber.Contains(docNum))
            {
                schedule.Forget(documentType, docNum);
                continue;
            }

            var lastFailedAtUtc = rows
                .Where(row => owing.Contains(row.Status))
                .Max(row => row.TimestampUtc);

            var firstRecordedAtUtc = rows.Min(row => row.TimestampUtc < row.CreatedAtUtc ? row.TimestampUtc : row.CreatedAtUtc);

            if (firstRecordedAtUtc < notBeforeUtc)
            {
                schedule.Forget(documentType, docNum);
                continue;
            }

            if (lastFailedAtUtc < cutoff || !schedule.IsDue(documentType, docNum, lastFailedAtUtc, nowUtc, options))
            {
                continue;
            }

            candidates.Add(new FiscalReceiptPickupCandidate(
                document,
                docNum,
                firstRecordedAtUtc,
                lastFailedAtUtc,
                new FiscalReceiptPickupRow(
                    latest.CardCode,
                    latest.CardName,
                    latest.DocTotal,
                    latest.VatSum,
                    latest.Currency,
                    latest.OriginalInvoiceNumber,
                    latest.Status,
                    latest.SourceSystem)));
        }

        return candidates
            .OrderBy(candidate => candidate.LastFailedAtUtc)
            .ThenBy(candidate => candidate.DocNum)
            .Take(Math.Max(1, options.BatchSize))
            .ToList();
    }

    /// <summary>
    /// The documents whose receipt was filed under a sale's own reference before they reached SAP, so a look-up by
    /// DocNum would only ever answer "no".
    /// </summary>
    private async Task<HashSet<int>> FindFiledUnderAnotherNumberAsync(
        FiscalReceiptPickupDocument document,
        IReadOnlyCollection<int> docNums,
        CancellationToken cancellationToken)
    {
        if (document.DocumentType == FiscalReceiptPickupDocument.CreditNote.DocumentType)
        {
            return await PerSaleCreditNoteRegistry.FindPerSaleDocNumsAsync(db, docNums, cancellationToken);
        }

        var filed = await ConsolidatedInvoiceRegistry.FindConsolidatedDocNumsAsync(db, docNums, cancellationToken);
        filed.UnionWith(await PerSaleInvoiceRegistry.FindPerSaleDocNumsAsync(db, docNums, cancellationToken));
        filed.UnionWith((await PerSaleInvoiceRegistry.FindReceiptPendingSalesAsync(db, docNums, cancellationToken)).Keys);
        return filed;
    }

    /// <summary>
    /// The start, in UTC, of the CAT day <see cref="FiscalReceiptPickupSettings.InvoicesFrom"/> names, for invoices;
    /// <see cref="DateTime.MinValue"/> otherwise. CAT is UTC+2 all year.
    /// </summary>
    private DateTime NotBeforeUtc(FiscalReceiptPickupDocument document)
        => document.DocumentType == FiscalReceiptPickupDocument.Invoice.DocumentType
           && settings.Value.InvoicesFrom is { } from
            ? DateTime.SpecifyKind(from.Date - TimeSpan.FromHours(2), DateTimeKind.Utc)
            : DateTime.MinValue;

    private void RecordMiss(FiscalReceiptPickupCandidate candidate)
        => schedule.RecordMiss(
            candidate.Document.DocumentType, candidate.DocNum, candidate.LastFailedAtUtc, Now(), settings.Value);

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    private static SyncFiscalTransactionRequest BuildSyncRequest(
        FiscalReceiptPickupCandidate candidate,
        FiscalReceiptSnapshot snapshot)
    {
        var document = candidate.Document;
        var failure = candidate.LatestFailure;
        var receipt = snapshot.ReceiptGlobalNo is { } number ? $"receipt {number}" : "a receipt";

        return new SyncFiscalTransactionRequest
        {
            // One row per document: a second find of the same receipt updates it rather than adding another.
            ClientTransactionId = $"fiscal-receipt-pickup-{document.DocumentType.ToLowerInvariant()}-{candidate.DocNum}",
            TimestampUtc = snapshot.TimestampUtc,
            DocNum = candidate.DocNum,
            DocumentType = document.DocumentType,
            Status = FiscalTransactionStatus.Fiscalised,
            Message = $"The fiscal device holds {receipt} for {document.Label} {candidate.DocNum}, which the log had recorded "
                      + $"as {failure.Status}; picked up automatically.",
            VerificationCode = snapshot.VerificationCode,
            QRCode = snapshot.QrCode,
            DeviceSerialNumber = snapshot.DeviceSerialNumber,
            DeviceId = snapshot.DeviceId,
            FiscalDay = snapshot.FiscalDay,
            ReceiptGlobalNo = snapshot.ReceiptGlobalNo,
            CardCode = failure.CardCode,
            CardName = failure.CardName,
            DocTotal = failure.DocTotal,
            VatSum = failure.VatSum,
            Currency = failure.Currency,
            OriginalInvoiceNumber = failure.OriginalInvoiceNumber,
            RawRequest = JsonSerializer.Serialize(new
            {
                document.DocumentType,
                candidate.DocNum,
                candidate.FirstRecordedAtUtc,
                candidate.LastFailedAtUtc,
                LatestStatus = failure.Status,
                LatestSourceSystem = failure.SourceSystem
            }),
            RawResponse = snapshot.RawResponseJson,
            SourceSystem = SourceSystem
        };
    }
}
