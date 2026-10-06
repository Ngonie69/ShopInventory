using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.CreditNotes;
using ShopInventory.Mappings;
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
    SapCreditNoteFiscaliser fiscaliser,
    IIdempotencyRequestStore idempotencyRequestStore,
    IAuditService auditService,
    IOptions<SAPSettings> sapSettings,
    IOptions<CreditNoteApprovalSettings> approvalSettings,
    ILogger<AddApprovedCreditNoteHandler> logger)
    : IRequestHandler<AddApprovedCreditNoteCommand, ErrorOr<AddApprovedCreditNoteResultDto>>
{
    private const string IdempotencyScope = "credit-note-approval-add";
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
                        Message = "The credit note could not be read back from SAP, so it was not fiscalised here. The scheduled credit-note fiscalisation will file it."
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
                ? fiscalisation.Skipped || fiscalisation.AlreadyFiscalised
                    ? " Already fiscalised."
                    : " Fiscalised."
                : $" Fiscalisation failed and has been logged for review: {fiscalisation.Message}"
            : string.Empty;

        return added + fiscal;
    }

    private async Task<CreditNoteApprovalFiscalisationDto> FiscaliseAsync(SAPCreditNote creditNote, AddApprovedCreditNoteCommand command)
    {
        var outcome = await fiscaliser.FiscaliseAsync(
            creditNote,
            new SapCreditNoteFiscalisationCaller(
                FiscalSourceSystem,
                "credit-note-approval-add",
                $"added from approval request {command.Code}",
                CaptureIncidents: true,
                command.UserId.ToString(),
                command.Username),
            CancellationToken.None);

        if (outcome.Result is not { } result)
        {
            return new CreditNoteApprovalFiscalisationDto
            {
                Attempted = true,
                Success = false,
                Message = outcome.Refusal ?? outcome.Exception?.Message
            };
        }

        return new CreditNoteApprovalFiscalisationDto
        {
            Attempted = true,
            Success = result.Success,
            Skipped = result.Skipped,
            AlreadyFiscalised = result.AlreadyFiscalised,
            Message = result.Message,
            ReceiptGlobalNo = result.ReceiptGlobalNo
        };
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
