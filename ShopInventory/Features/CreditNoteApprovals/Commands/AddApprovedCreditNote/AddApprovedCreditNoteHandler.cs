using System.Diagnostics;
using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Idempotency;
using ShopInventory.Common.Stock;
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
///
/// A stage-scoped caller — the wash bay — adds only what stands at its own stages, as it decides only
/// those. The scope is checked on the live request and, for a repeat, on the stage the first answer
/// recorded, because SAP deletes the request once it is added.
/// </remarks>
public sealed class AddApprovedCreditNoteHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sap,
    ICreditNoteApprovalStageScope stageScope,
    ICreditNoteProjectionSyncService projectionSync,
    SapCreditNoteFiscaliser fiscaliser,
    IStockLedger stockLedger,
    IIdempotencyRequestStore idempotencyRequestStore,
    ICreditNoteAlreadyCreditedIndex alreadyCreditedIndex,
    ISapApprovalLookups lookups,
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
        var clock = Stopwatch.StartNew();
        try
        {
            // Before the claim, so a wash bay with no stage configured is refused without holding it.
            var scope = await stageScope.ResolveAsync(command.UserId, cancellationToken);
            if (scope.IsError)
            {
                return scope.Errors;
            }

            // The claim comes next and is keyed on the request alone, whoever clicks: a draft is
            // converted exactly once, and a retry of a call that timed out after SAP converted it
            // replays the first answer — the credit note it became — instead of "already added".
            var acquired = await idempotencyRequestStore.TryAcquireAsync<AddApprovedCreditNoteResultDto>(
                IdempotencyScope,
                command.Code.ToString(),
                new { command.Code },
                cancellationToken);

            switch (acquired.Outcome)
            {
                // The request is gone from SAP by now, so the replay is scoped by the stage it was added at.
                case IdempotencyAcquireOutcome.ReplayAvailable when acquired.Response is not null:
                    return scope.Value.Admits(acquired.Response.StageCode)
                        ? acquired.Response
                        : Errors.CreditNoteApproval.OutsideStageScope(command.Code, scope.Value.Describe());
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

            // Before any state is described, as for a decision: a request outside the caller's stages
            // is not theirs to learn anything about.
            if (!scope.Value.Admits(request.CurrentStage))
            {
                return Errors.CreditNoteApproval.OutsideStageScope(command.Code, scope.Value.Describe());
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

            // The line the created credit note must lie past, read alongside the draft rather than
            // after it: it needs nothing the draft says. SAP names the created document nowhere — the
            // approval request is deleted by a successful add, and its ObjectEntry is never populated
            // even before that — so it is recognised afterwards as a credit note past this line.
            var newestBeforeTask = TryReadNewestCreditNoteDocEntryAsync();

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

            // Approved in SAP is not the same as not yet credited: the credit may have been keyed straight
            // into SAP while the draft waited, and adding this one would pay the customer twice. Checked on
            // the draft as raised, before any quantity is changed.
            var notCredited = await CreditNoteAlreadyCredited.RefuseIfCreditedAsync(
                sap, lookups: null, draft, canReject: false, logger, cancellationToken);
            if (notCredited.IsError)
            {
                await TryAuditAsync(command, draft, false, notCredited.FirstError.Description);
                return notCredited.Errors;
            }

            // The quantities the person typed, checked against the draft they were typed against.
            var quantityChanges = CreditNoteDraftQuantityPlan.Validate(draft.DocumentLines ?? [], command.LineQuantities);
            if (quantityChanges.IsError)
            {
                return quantityChanges.Errors;
            }

            IReadOnlyList<SapDraftLineQuantityChange> lineChanges = [];
            if (quantityChanges.Value.Count > 0)
            {
                // Batches come back only on a whole-draft read, so this is a second read of the draft.
                var allocations = await sap.GetDraftAllocationsAsync(draftEntry, cancellationToken);
                if (allocations is null)
                {
                    return Errors.CreditNoteApproval.DraftMissing(draftEntry);
                }

                var plan = CreditNoteDraftQuantityPlan.Build(quantityChanges.Value, allocations);
                if (plan.IsError)
                {
                    return plan.Errors;
                }

                lineChanges = plan.Value;
            }

            // What is left to credit on the invoice the draft reverses. ZIMRA refuses a credit note past it,
            // and SAP adds one anyway when lines were typed onto a copied memo, so it would be added and
            // never fiscalised. Read before any quantity is changed — the lines' base links do not move —
            // and weighed against the total the draft will be added with.
            CreditNoteInvoiceRoom.Room? invoiceRoom;
            try
            {
                invoiceRoom = await CreditNoteInvoiceRoom.ReadAsync(sap, draft, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not read what is left to credit on the invoice behind draft {DraftEntry}; refusing the add", draftEntry);
                await TryAuditAsync(command, draft, false, Errors.CreditNoteApproval.InvoiceRoomUnchecked.Description);
                return Errors.CreditNoteApproval.InvoiceRoomUnchecked;
            }

            if (lineChanges.Count == 0)
            {
                var fits = CreditNoteInvoiceRoom.Check(draft, invoiceRoom, logger);
                if (fits.IsError)
                {
                    await TryAuditAsync(command, draft, false, fits.FirstError.Description);
                    return fits.Errors;
                }
            }

            var newestBefore = await newestBeforeTask;
            var checkedAt = clock.Elapsed;

            // The last safe abort: nothing has reached SAP yet.
            cancellationToken.ThrowIfCancellationRequested();

            // From here on the writes run on a token the caller cannot cancel: a change of quantities
            // abandoned halfway would leave the draft edited and not added.
            if (lineChanges.Count > 0)
            {
                var changed = await ChangeQuantitiesAsync(command, draft, lineChanges, quantityChanges.Value);
                if (changed.IsError)
                {
                    return changed.Errors;
                }

                // The add is recognised afterwards by the draft's total, which the new quantities changed.
                draft = changed.Value;

                // The new quantities are on the draft, so its total is the one SAP would add. Past the
                // invoice it is refused here, with the edit left on the draft for the next attempt.
                var fits = CreditNoteInvoiceRoom.Check(draft, invoiceRoom, logger);
                if (fits.IsError)
                {
                    await TryAuditAsync(command, draft, false, $"New quantities saved on the draft, not added: {fits.FirstError.Description}");
                    return Error.Conflict(
                        fits.FirstError.Code,
                        $"The new quantities were saved on draft {draftEntry}, but the credit note was not added. {fits.FirstError.Description}");
                }
            }

            // The invoice the credit note reverses was read for the guard above; the fiscaliser files the
            // memo against that same invoice, so it is handed on rather than read a second time.
            var invoiceRead = approvalSettings.Value.FiscaliseAfterAdd
                              && invoiceRoom is not null
                              && SapCreditNoteOriginalInvoice.BaseLinkOf(draft).SingleInvoiceDocEntry is int invoiceEntry
                              && invoiceEntry == invoiceRoom.Invoice.DocEntry
                ? new SapInvoiceRead(invoiceEntry, Task.FromResult<Invoice?>(invoiceRoom.Invoice))
                : null;

            int? createdDocEntry;
            try
            {
                createdDocEntry = await sap.SaveDraftToDocumentAsync(draftEntry, CancellationToken.None);
            }
            catch (SapRequestRejectedException rejected)
            {
                logger.LogWarning(rejected, "SAP refused to add draft {DraftEntry} for approval request {Code}", draftEntry, command.Code);
                await TryAuditAsync(command, draft, false, $"SAP refused: {rejected.SapMessage}");
                return CreditNoteDraftAttachments.IsAttachmentsFolderRefusal(rejected.SapMessage)
                    ? Errors.CreditNoteApproval.AddBlockedByAttachmentsFolder
                    : Errors.CreditNoteApproval.SapRejected(rejected.SapMessage);
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

            var savedAt = clock.Elapsed;

            // A new credit note can be the one that already credits another held draft.
            alreadyCreditedIndex.Invalidate();

            // When SAP's answer names no document — the usual case, a 204 — the read that finds the
            // created credit note returns it whole, so it is not read a second time.
            var identified = createdDocEntry is null
                ? await TryIdentifyCreatedCreditNoteAsync(draft, newestBefore)
                : null;
            var docEntry = createdDocEntry ?? identified?.DocEntry;

            var creditNote = identified
                ?? (createdDocEntry is int entry ? await TryReadCreditNoteAsync(entry) : null);

            // The memo is created under the app's SAP account, so its Remarks name who raised the draft.
            // The read-back copy keeps the remarks it was added with: they are the reason the fiscal
            // receipt files, and the sync job picks the new remarks up for the projection.
            if (docEntry is int addedEntry)
            {
                await TryNoteRaisedByAsync(addedEntry, creditNote?.Comments ?? draft.Comments, request.OriginatorID ?? draft.UserSign, command.Code);
            }

            if (creditNote is not null)
            {
                await TryProjectAsync(creditNote);
                await TryReturnUnitsAsync(creditNote);
            }

            var readBackAt = clock.Elapsed;

            var fiscalisation = approvalSettings.Value.FiscaliseAfterAdd
                ? creditNote is null
                    ? new CreditNoteApprovalFiscalisationDto
                    {
                        Attempted = false,
                        Skipped = true,
                        Message = "The credit note could not be read back from SAP, so it was not fiscalised here. The scheduled credit-note fiscalisation will file it."
                    }
                    : await FiscaliseAsync(creditNote, command, invoiceRead)
                : new CreditNoteApprovalFiscalisationDto
                {
                    Attempted = false,
                    Skipped = true,
                    Message = "Fiscalisation after add is switched off."
                };

            var fiscalisedAt = clock.Elapsed;

            var result = new AddApprovedCreditNoteResultDto
            {
                Code = command.Code,
                DraftEntry = draftEntry,
                CreditNoteDocEntry = creditNote?.DocEntry ?? docEntry,
                CreditNoteDocNum = creditNote?.DocNum,
                Resolved = docEntry is not null,
                StageCode = request.CurrentStage,
                QuantityChanges = quantityChanges.Value.ToList(),
                Fiscalisation = fiscalisation,
                Message = Describe(creditNote, docEntry, fiscalisation, quantityChanges.Value)
            };

            await TryAuditAsync(command, draft, true, result.Message);

            // The API keeps no per-call SAP timings, so this line is how a slow add is attributed.
            logger.LogInformation(
                "Added credit note {DocNum} from approval request {Code} in {TotalMs} ms: checks {CheckMs} ms, "
                + "SAP add {SaveMs} ms, read back {ReadBackMs} ms, fiscalise {FiscalMs} ms",
                creditNote?.DocNum, command.Code, (long)clock.Elapsed.TotalMilliseconds,
                (long)checkedAt.TotalMilliseconds,
                (long)(savedAt - checkedAt).TotalMilliseconds,
                (long)(readBackAt - savedAt).TotalMilliseconds,
                (long)(fiscalisedAt - readBackAt).TotalMilliseconds);

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

    private static string Describe(
        SAPCreditNote? creditNote,
        int? docEntry,
        CreditNoteApprovalFiscalisationDto fiscalisation,
        IReadOnlyList<CreditNoteLineQuantityChangeDto> quantityChanges)
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
                : $" Fiscalisation failed and has been logged for review: {fiscalisation.UserMessage ?? fiscalisation.Message}"
            : string.Empty;

        var quantities = quantityChanges.Count > 0
            ? $" Quantities changed before adding: {CreditNoteDraftQuantityPlan.Describe(quantityChanges)}."
            : string.Empty;

        return added + quantities + fiscal;
    }

    /// <summary>
    /// Writes the new quantities to the draft and reads it back. Returns the draft as it now stands,
    /// or why it must not be added.
    /// </summary>
    /// <remarks>
    /// SAP keeps an approved draft approved when its quantities change, and the credit note it adds
    /// carries them: measured on KEFALOS_TEST_3, 2026-11-09 (draft 79079, 26 → 24, credit note 84478).
    /// The read-back is the check that both still hold here.
    /// </remarks>
    private async Task<ErrorOr<SAPCreditNote>> ChangeQuantitiesAsync(
        AddApprovedCreditNoteCommand command,
        SAPCreditNote draft,
        IReadOnlyList<SapDraftLineQuantityChange> lineChanges,
        IReadOnlyList<CreditNoteLineQuantityChangeDto> quantityChanges)
    {
        var described = CreditNoteDraftQuantityPlan.Describe(quantityChanges);
        try
        {
            await sap.UpdateDraftLineQuantitiesAsync(draft.DocEntry, lineChanges, CancellationToken.None);
        }
        catch (SapRequestRejectedException rejected)
        {
            logger.LogWarning(rejected, "SAP refused new quantities on draft {DraftEntry} for approval request {Code}", draft.DocEntry, command.Code);
            await TryAuditAsync(command, draft, false, $"SAP refused the new quantities ({described}): {rejected.SapMessage}");
            return Errors.CreditNoteApproval.SapRejected(rejected.SapMessage);
        }
        catch (Exception exception)
        {
            // Not retried: the change may have landed, and batches are moved by differences.
            logger.LogError(exception, "Changing quantities on draft {DraftEntry} got no clear answer from SAP", draft.DocEntry);
            await TryAuditAsync(command, draft, false, $"No clear answer from SAP to the new quantities ({described}): {exception.Message}");
            return Errors.CreditNoteApproval.QuantitiesNotApplied(draft.DocEntry);
        }

        var after = await TryReadDraftAsync(draft.DocEntry);
        var byLine = after?.DocumentLines?.ToDictionary(line => line.LineNum);
        if (after is null || byLine is null
            || quantityChanges.Any(change => !byLine.TryGetValue(change.LineNum, out var line) || line.Quantity != change.To))
        {
            await TryAuditAsync(command, draft, false, $"New quantities ({described}) were not confirmed on the draft");
            return Errors.CreditNoteApproval.QuantitiesNotApplied(draft.DocEntry);
        }

        if (!CreditNoteApprovalProjection.IsOpen(after)
            || (!string.IsNullOrWhiteSpace(after.AuthorizationStatus)
                && !string.Equals(after.AuthorizationStatus, SapDocumentAuthorizationStatuses.Approved, StringComparison.OrdinalIgnoreCase)))
        {
            var state = CreditNoteApprovalProjection.IsOpen(after)
                ? SapEnumNames.StripPrefix(after.AuthorizationStatus, "das")
                : "closed";
            await TryAuditAsync(command, after, false, $"New quantities ({described}) saved, but the draft is now {state}");
            return Errors.CreditNoteApproval.ApprovalLostAfterQuantityChange(draft.DocEntry, state);
        }

        logger.LogInformation(
            "Changed quantities on draft {DraftEntry} for approval request {Code} before adding: {Changes}; total {Before} → {After}",
            draft.DocEntry, command.Code, described, draft.DocTotal, after.DocTotal);
        return after;
    }

    private async Task<CreditNoteApprovalFiscalisationDto> FiscaliseAsync(
        SAPCreditNote creditNote,
        AddApprovedCreditNoteCommand command,
        SapInvoiceRead? invoiceRead)
    {
        // The person adding it is who raised it here: the memo is posted under ShopInventory's own SAP account,
        // which the platform would otherwise name.
        var raisedBy = await FiscalReceiptOperatorLookup.ResolveAsync(
            context, command.UserId, command.Username, logger, CancellationToken.None);

        var outcome = await fiscaliser.FiscaliseAsync(
            creditNote,
            new SapCreditNoteFiscalisationCaller(
                FiscalSourceSystem,
                "credit-note-approval-add",
                $"added from approval request {command.Code}",
                CaptureIncidents: true,
                command.UserId.ToString(),
                command.Username,
                raisedBy),
            CancellationToken.None,
            invoiceRead);

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
            UserMessage = result.Success ? null : result.UserMessage,
            ReceiptGlobalNo = result.ReceiptGlobalNo
        };
    }

    /// <summary>
    /// Appends "Raised by …" to the added credit memo's Remarks. Best effort: the credit note exists
    /// whatever happens here, and a memo without the name is only less informative.
    /// </summary>
    private async Task TryNoteRaisedByAsync(int docEntry, string? comments, int? originatorId, int code)
    {
        SAPUser? originator = null;
        if (originatorId is int userId)
        {
            try
            {
                originator = await lookups.GetUserAsync(userId, CancellationToken.None);
            }
            catch (Exception exception)
            {
                // Still worth naming by key: "SAP user 31" can be looked up, a blank cannot.
                logger.LogWarning(exception, "Could not read SAP user {UserId}, who raised approval request {Code}", userId, code);
            }
        }

        var raisedBy = CreditNoteRaisedByRemark.Describe(originator, originatorId);
        if (raisedBy is null)
        {
            logger.LogWarning("Approval request {Code} names nobody who raised it, so credit note {DocEntry}'s remarks are left alone", code, docEntry);
            return;
        }

        var remarks = CreditNoteRaisedByRemark.Append(comments, raisedBy);
        if (remarks is null)
        {
            return;
        }

        try
        {
            await sap.UpdateCreditNoteFieldsAsync(
                docEntry,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["Comments"] = remarks },
                CancellationToken.None);
            logger.LogInformation("Credit note {DocEntry} from approval request {Code}: remarks now say \"{RaisedBy}\"", docEntry, code, raisedBy);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Credit note {DocEntry} was added, but \"{RaisedBy}\" could not be written to its remarks", docEntry, raisedBy);
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

    /// <summary>
    /// Puts the units the credit note brought back into today's local stock, as a credit note raised
    /// here already does.
    /// </summary>
    /// <remarks>
    /// Without this, Local stock read low for every approved return until the hourly SAP check or
    /// the next morning's fetch restated it, and tills were refused stock that was on the shelf. SAP
    /// has already moved the stock, so the lines are read from the credit note itself. If it could not
    /// be read back, the hourly check catches it up. Best effort: the credit note is in SAP whatever
    /// happens here.
    /// </remarks>
    private async Task TryReturnUnitsAsync(SAPCreditNote creditNote)
    {
        var returned = SapCreditNoteStockReturn.LinesOf(creditNote);
        if (returned.Count == 0)
        {
            return;
        }

        try
        {
            await stockLedger.ReleaseAsync(
                returned,
                $"credit note {creditNote.DocNum}",
                SapCreditNoteStockReturn.DocumentKey(creditNote.DocEntry),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Credit note {DocNum} was added, but its units could not be returned to local stock",
                creditNote.DocNum);
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

    private async Task<int?> TryReadNewestCreditNoteDocEntryAsync()
    {
        try
        {
            return await sap.GetNewestCreditNoteDocEntryAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read the newest credit note before the add");
            return null;
        }
    }

    /// <summary>
    /// The credit note the add produced, identified as one of this customer's past the newest credit
    /// note before the add, carrying the draft's total. It comes back whole, lines included.
    /// </summary>
    /// <remarks>
    /// Deliberately not a match on DocNum: drafts and credit notes number from different series, and
    /// on KEFALOS_TEST_3 the credit note carrying a converted draft's DocNum belonged to an entirely
    /// different customer. Adopting that would have put somebody else's document on this screen.
    /// </remarks>
    private async Task<SAPCreditNote?> TryIdentifyCreatedCreditNoteAsync(SAPCreditNote draft, int? newestBefore)
    {
        if (string.IsNullOrWhiteSpace(draft.CardCode))
        {
            return null;
        }

        SAPCreditNote? newestAfter;
        try
        {
            newestAfter = await sap.GetNewestCreditNoteForCustomerAsync(draft.CardCode, newestBefore ?? 0, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read the newest credit note for {CardCode} after the add", draft.CardCode);
            return null;
        }

        // Past the line is the client's filter; it is checked again so a client that ignored it cannot
        // hand back a document that was already there.
        if (newestAfter is null || newestAfter.DocEntry <= (newestBefore ?? 0))
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

        return newestAfter;
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
