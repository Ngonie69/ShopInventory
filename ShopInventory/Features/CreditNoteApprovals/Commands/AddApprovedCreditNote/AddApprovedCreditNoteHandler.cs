using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.CreditNotes;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNoteApprovals.Commands.AddApprovedCreditNote;

/// <summary>
/// Adds an approved draft: <c>DraftsService_SaveDraftToDocument</c>, then the credit note is read back,
/// written through to the projection so the Credit Notes list shows it at once, and fiscalised — a
/// document added through the Service Layer never passes the platform's B1 print bridge.
/// </summary>
/// <remarks>
/// The add is money and it happens once: one idempotency key per request, whoever clicks, and the SAP
/// call runs on a token the caller cannot cancel. Everything after the add is best effort — a fiscal
/// failure is an Exception Center incident, a projection failure is repaired by the sync job — and
/// none of it can fail the add, because the credit note already exists.
/// </remarks>
public sealed class AddApprovedCreditNoteHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sap,
    ICreditNoteProjectionSyncService projectionSync,
    IFiscalizationService fiscalizationService,
    ISender sender,
    IIdempotencyRequestStore idempotencyRequestStore,
    IAuditService auditService,
    IOptions<SAPSettings> sapSettings,
    IOptions<CreditNoteApprovalSettings> approvalSettings,
    ILogger<AddApprovedCreditNoteHandler> logger)
    : IRequestHandler<AddApprovedCreditNoteCommand, ErrorOr<AddApprovedCreditNoteResultDto>>
{
    private const string IdempotencyScope = "credit-note-approval-add";
    private const string FiscalDocumentType = "CreditNote";
    private const string FiscalSourceSystem = "CreditNoteApprovalAdd";

    public async Task<ErrorOr<AddApprovedCreditNoteResultDto>> Handle(
        AddApprovedCreditNoteCommand command,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
        {
            return Errors.CreditNoteApproval.SapDisabled;
        }

        long? idempotencyRequestId = null;
        var release = false;
        try
        {
            // The claim comes first and is keyed on the request alone, whoever clicks: a draft is
            // converted exactly once, and a retry of a call that timed out after SAP converted it
            // replays the first answer — the credit note it became — instead of "already added".
            var acquired = await idempotencyRequestStore.TryAcquireAsync<AddApprovedCreditNoteResultDto>(
                IdempotencyScope,
                command.Code.ToString(),
                new { command.Code },
                cancellationToken);

            switch (acquired.Outcome)
            {
                case IdempotencyAcquireOutcome.ReplayAvailable when acquired.Response is not null:
                    return acquired.Response;
                case IdempotencyAcquireOutcome.InProgress:
                    return Errors.CreditNoteApproval.AddInProgress;
                case IdempotencyAcquireOutcome.RequestMismatch:
                    return Errors.Idempotency.RequestMismatch("credit note add");
                case IdempotencyAcquireOutcome.Acquired:
                    idempotencyRequestId = acquired.RequestId;
                    release = true;
                    break;
            }

            var request = await sap.GetApprovalRequestAsync(command.Code, cancellationToken);
            if (request is null || !string.Equals(request.ObjectType, SapObjectTypes.CreditNote, StringComparison.Ordinal))
            {
                return Errors.CreditNoteApproval.NotFound(command.Code);
            }

            if (SapApprovalRequestStatuses.IsGenerated(request.Status))
            {
                return Errors.CreditNoteApproval.AlreadyAdded(command.Code, request.ObjectEntry);
            }

            if (!string.Equals(request.Status, SapApprovalRequestStatuses.Approved, StringComparison.OrdinalIgnoreCase))
            {
                return Errors.CreditNoteApproval.NotApproved(SapApprovalRequestStatuses.ToDisplay(request.Status));
            }

            if (request.DraftEntry is not int draftEntry || draftEntry <= 0)
            {
                return Errors.CreditNoteApproval.NoDraft(command.Code);
            }

            var draft = await sap.GetCreditNoteDraftAsync(draftEntry, cancellationToken);
            if (draft is null)
            {
                return Errors.CreditNoteApproval.DraftMissing(draftEntry);
            }

            if (!string.Equals(draft.DocObjectCode, SapDocObjectCodes.CreditNotes, StringComparison.OrdinalIgnoreCase))
            {
                return Errors.CreditNoteApproval.NotACreditNoteDraft(draftEntry);
            }

            if (!CreditNoteApprovalProjection.IsOpen(draft))
            {
                return Errors.CreditNoteApproval.DraftNotOpen;
            }

            if (!string.IsNullOrWhiteSpace(draft.AuthorizationStatus)
                && !string.Equals(draft.AuthorizationStatus, SapDocumentAuthorizationStatuses.Approved, StringComparison.OrdinalIgnoreCase))
            {
                return Errors.CreditNoteApproval.NotApproved(
                    $"approved, but its draft's own state is {SapEnumNames.StripPrefix(draft.AuthorizationStatus, "das")}");
            }

            // Which credit note this customer had before the add, so the one it produces can be told
            // apart afterwards. SAP names the created document nowhere: the approval request is deleted
            // by a successful add, and its ObjectEntry is never populated even before that.
            var newestBefore = await TryReadNewestCreditNoteAsync(draft.CardCode);

            // The last safe abort: nothing has reached SAP yet.
            cancellationToken.ThrowIfCancellationRequested();

            int? createdDocEntry;
            try
            {
                createdDocEntry = await sap.SaveDraftToDocumentAsync(draftEntry, CancellationToken.None);
            }
            catch (SapRequestRejectedException rejected)
            {
                logger.LogWarning(rejected, "SAP refused to add draft {DraftEntry} for approval request {Code}", draftEntry, command.Code);
                await TryAuditAsync(command, draft, false, $"SAP refused: {rejected.SapMessage}");
                return Errors.CreditNoteApproval.SapRejected(rejected.SapMessage);
            }
            catch (Exception exception)
            {
                // The add may or may not have happened, and the approval request cannot say: SAP
                // deletes it the moment the draft converts. The draft itself is the witness — it goes
                // from bost_Open to bost_Close — and it survives either way.
                var draftAfter = await TryReadDraftAsync(draftEntry);
                if (draftAfter is null || CreditNoteApprovalProjection.IsOpen(draftAfter))
                {
                    logger.LogError(exception, "Adding draft {DraftEntry} for approval request {Code} got no clear answer from SAP", draftEntry, command.Code);
                    await TryAuditAsync(command, draft, false, $"No clear answer from SAP: {exception.Message}");
                    return Errors.CreditNoteApproval.AddUncertain;
                }

                logger.LogWarning(exception, "Draft {DraftEntry} was added although the call failed; it is closed in SAP", draftEntry);
                createdDocEntry = null;
            }

            var docEntry = createdDocEntry ?? await TryIdentifyCreatedCreditNoteAsync(draft, newestBefore);

            var creditNote = docEntry is int entry ? await TryReadCreditNoteAsync(entry) : null;
            if (creditNote is not null)
            {
                await TryProjectAsync(creditNote);
            }

            var fiscalisation = approvalSettings.Value.FiscaliseAfterAdd
                ? creditNote is null
                    ? new CreditNoteApprovalFiscalisationDto
                    {
                        Attempted = false,
                        Skipped = true,
                        Message = "The credit note could not be read back from SAP, so it was not fiscalised. Fiscalise it from the Credit Notes list."
                    }
                    : await FiscaliseAsync(creditNote, command)
                : new CreditNoteApprovalFiscalisationDto
                {
                    Attempted = false,
                    Skipped = true,
                    Message = "Fiscalisation after add is switched off."
                };

            var result = new AddApprovedCreditNoteResultDto
            {
                Code = command.Code,
                DraftEntry = draftEntry,
                CreditNoteDocEntry = creditNote?.DocEntry ?? docEntry,
                CreditNoteDocNum = creditNote?.DocNum,
                Resolved = docEntry is not null,
                Fiscalisation = fiscalisation,
                Message = Describe(creditNote, docEntry, fiscalisation)
            };

            await TryAuditAsync(command, draft, true, result.Message);

            if (idempotencyRequestId.HasValue)
            {
                await idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, result, CancellationToken.None);
                release = false;
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.CreditNoteApproval.Cancelled;
        }
        catch (SapRequestRejectedException rejected)
        {
            return Errors.CreditNoteApproval.SapRejected(rejected.SapMessage);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not add the draft for SAP approval request {Code}", command.Code);
            return Errors.CreditNoteApproval.SapUnavailable(exception.Message);
        }
        finally
        {
            if (release && idempotencyRequestId.HasValue)
            {
                try
                {
                    await idempotencyRequestStore.ReleaseAsync(idempotencyRequestId.Value, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Failed to release the credit note add lock for request {Code}", command.Code);
                }
            }
        }
    }

    private static string Describe(SAPCreditNote? creditNote, int? docEntry, CreditNoteApprovalFiscalisationDto fiscalisation)
    {
        var added = creditNote is not null
            ? $"Credit note #{creditNote.DocNum} added to SAP."
            : docEntry is int entry
                ? $"Credit note DocEntry {entry} added to SAP."
                : "The draft was added, but SAP did not say which credit note it became; the Credit Notes list will show it within a few minutes.";

        var fiscal = fiscalisation.Attempted
            ? fiscalisation.Success
                ? fiscalisation.Skipped ? " Already fiscalised." : " Fiscalised."
                : $" Fiscalisation failed and has been logged for review: {fiscalisation.Message}"
            : string.Empty;

        return added + fiscal;
    }

    private async Task<CreditNoteApprovalFiscalisationDto> FiscaliseAsync(SAPCreditNote creditNote, AddApprovedCreditNoteCommand command)
    {
        // The line-level base entry is what a credit memo raised against an invoice carries; the header
        // one is not selected on credit notes. Advisory either way — the platform reads the link itself.
        var originalInvoiceDocEntry = creditNote.BaseEntry
            ?? creditNote.DocumentLines?.FirstOrDefault(line => line.BaseType == 13 && line.BaseEntry.HasValue)?.BaseEntry;

        var document = new InvoiceDto
        {
            DocEntry = creditNote.DocEntry,
            DocNum = creditNote.DocNum,
            CardCode = creditNote.CardCode,
            CardName = creditNote.CardName,
            DocTotal = Math.Abs(creditNote.DocTotal),
            VatSum = Math.Abs(creditNote.VatSum),
            DocCurrency = creditNote.DocCurrency,
            Comments = creditNote.Comments,
            Lines = creditNote.DocumentLines?.Select(line => new InvoiceLineDto
            {
                LineNum = line.LineNum,
                ItemCode = line.ItemCode,
                ItemDescription = line.ItemDescription,
                Quantity = Math.Abs(line.Quantity),
                UnitPrice = line.UnitPrice,
                LineTotal = Math.Abs(line.LineTotal),
                TaxCode = line.TaxCode,
                WarehouseCode = line.WarehouseCode
            }).ToList()
        };

        var customer = new CustomerFiscalDetails { CustomerName = creditNote.CardName };
        var originalInvoiceNumber = originalInvoiceDocEntry?.ToString() ?? string.Empty;

        try
        {
            var result = await fiscalizationService.FiscalizeCreditNoteAsync(document, originalInvoiceNumber, customer, CancellationToken.None);

            await TryRecordFiscalTransactionAsync(creditNote, document, originalInvoiceNumber, result, command);

            if (!result.Success && !result.Skipped)
            {
                await CreditNoteFiscalisationIncidents.CaptureAsync(
                    context, logger, $"SAP-CN-{creditNote.DocNum}", creditNote.DocNum, creditNote.CardCode ?? string.Empty,
                    result.Message ?? "Fiscalisation failed for the credit note.", CancellationToken.None);
            }

            return new CreditNoteApprovalFiscalisationDto
            {
                Attempted = true,
                Success = result.Success,
                Skipped = result.Skipped,
                Message = result.Message,
                ReceiptGlobalNo = result.ReceiptGlobalNo
            };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Error fiscalising credit note {DocNum} added from approval request {Code}", creditNote.DocNum, command.Code);
            await CreditNoteFiscalisationIncidents.CaptureAsync(
                context, logger, $"SAP-CN-{creditNote.DocNum}", creditNote.DocNum, creditNote.CardCode ?? string.Empty,
                exception.Message, CancellationToken.None);

            return new CreditNoteApprovalFiscalisationDto { Attempted = true, Success = false, Message = exception.Message };
        }
    }

    /// <summary>
    /// The fiscal transaction row is what the Credit Notes list reads to say "Fiscalised"; without it a
    /// perfectly fiscalised document shows as owed a receipt.
    /// </summary>
    private async Task TryRecordFiscalTransactionAsync(
        SAPCreditNote creditNote,
        InvoiceDto document,
        string originalInvoiceNumber,
        FiscalizationResult result,
        AddApprovedCreditNoteCommand command)
    {
        try
        {
            var timestampUtc = DateTime.UtcNow;
            var recorded = await sender.Send(
                new SyncFiscalTransactionCommand(
                    new SyncFiscalTransactionRequest
                    {
                        ClientTransactionId = $"credit-note-approval-add-{creditNote.DocNum}-{timestampUtc:yyyyMMddHHmmssfffffff}",
                        TimestampUtc = timestampUtc,
                        DocNum = creditNote.DocNum,
                        DocumentType = FiscalDocumentType,
                        Status = result.Skipped ? "Fiscalised" : result.Success ? "Success" : "Failed",
                        Message = result.Message,
                        VerificationCode = result.VerificationCode,
                        QRCode = result.QRCode,
                        DeviceSerialNumber = result.DeviceSerial,
                        FiscalDay = result.FiscalDayNo,
                        ReceiptGlobalNo = int.TryParse(result.ReceiptGlobalNo, out var receiptNo) && receiptNo > 0 ? receiptNo : null,
                        CardCode = creditNote.CardCode,
                        CardName = creditNote.CardName,
                        DocTotal = document.DocTotal,
                        VatSum = document.VatSum,
                        Currency = creditNote.DocCurrency,
                        OriginalInvoiceNumber = string.IsNullOrWhiteSpace(originalInvoiceNumber) ? null : originalInvoiceNumber,
                        RawRequest = JsonSerializer.Serialize(new { Document = document }),
                        RawResponse = JsonSerializer.Serialize(result),
                        SourceSystem = FiscalSourceSystem
                    },
                    command.UserId.ToString(),
                    command.Username),
                CancellationToken.None);

            if (recorded.IsError)
            {
                logger.LogWarning(
                    "Fiscal transaction for credit note {DocNum} was not recorded: {Errors}",
                    creditNote.DocNum,
                    string.Join("; ", recorded.Errors.Select(error => error.Description)));
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Fiscal transaction for credit note {DocNum} was not recorded", creditNote.DocNum);
        }
    }

    private async Task TryProjectAsync(SAPCreditNote creditNote)
    {
        try
        {
            await projectionSync.UpsertAsync([creditNote], CancellationToken.None);
        }
        catch (Exception exception)
        {
            // SAP is authoritative; the clustered sync job repairs the projection within minutes.
            logger.LogWarning(exception, "Failed to write credit note {DocEntry} through to the local projection", creditNote.DocEntry);
        }
    }

    private async Task<SAPCreditNote?> TryReadDraftAsync(int draftEntry)
    {
        try
        {
            return await sap.GetCreditNoteDraftAsync(draftEntry, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read draft {DraftEntry} back after the add", draftEntry);
            return null;
        }
    }

    private async Task<SAPCreditNote?> TryReadNewestCreditNoteAsync(string? cardCode)
    {
        if (string.IsNullOrWhiteSpace(cardCode))
        {
            return null;
        }

        try
        {
            return await sap.GetNewestCreditNoteForCustomerAsync(cardCode, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read the newest credit note for {CardCode}", cardCode);
            return null;
        }
    }

    /// <summary>
    /// The credit note the add produced, identified as a document this customer did not have before
    /// that carries the draft's total.
    /// </summary>
    /// <remarks>
    /// Deliberately not a match on DocNum: drafts and credit notes number from different series, and
    /// on KEFALOS_TEST_3 the credit note carrying a converted draft's DocNum belonged to an entirely
    /// different customer. Adopting that would have put somebody else's document on this screen.
    /// </remarks>
    private async Task<int?> TryIdentifyCreatedCreditNoteAsync(SAPCreditNote draft, SAPCreditNote? newestBefore)
    {
        var newestAfter = await TryReadNewestCreditNoteAsync(draft.CardCode);
        if (newestAfter is null || newestAfter.DocEntry <= (newestBefore?.DocEntry ?? 0))
        {
            return null;
        }

        if (newestAfter.DocTotal != draft.DocTotal)
        {
            logger.LogWarning(
                "Credit note {DocEntry} is the newest for {CardCode} since the add but its total {Total} does not "
                + "match draft {DraftEntry}'s {DraftTotal}, so it is not claimed as the one that was created",
                newestAfter.DocEntry, draft.CardCode, newestAfter.DocTotal, draft.DocEntry, draft.DocTotal);
            return null;
        }

        return newestAfter.DocEntry;
    }

    private async Task<SAPCreditNote?> TryReadCreditNoteAsync(int docEntry)
    {
        try
        {
            return await sap.GetCreditNoteByDocEntryAsync(docEntry, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read credit note {DocEntry} back after the add", docEntry);
            return null;
        }
    }

    private async Task TryAuditAsync(AddApprovedCreditNoteCommand command, SAPCreditNote draft, bool success, string outcome)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.AddApprovedCreditNote,
                "SapApprovalRequest",
                command.Code.ToString(),
                $"Add approved credit memo draft {draft.DocEntry} ({draft.CardCode} {draft.DocTotal:N2} {draft.DocCurrency}) for SAP approval request {command.Code}. {outcome}",
                success,
                success ? null : outcome);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to audit the add for approval request {Code}", command.Code);
        }
    }
}
