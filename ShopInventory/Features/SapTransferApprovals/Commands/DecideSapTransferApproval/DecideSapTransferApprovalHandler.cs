using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.CreditNoteApprovals;
using ShopInventory.Features.CreditNoteApprovals.Commands.DecideCreditNoteApproval;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.SapTransferApprovals.Commands.DecideSapTransferApproval;

/// <summary>
/// Approves or rejects a SAP approval request that holds an inventory transfer or transfer request draft
/// keyed in the B1 client. The decision goes to SAP as the service approver, exactly as a credit memo
/// decision does, and the remarks name the person who clicked.
/// </summary>
/// <remarks>
/// Production, 2026-11-23: the app's credit memo decisions at stage 44 'Factory Washbay' (75 since
/// 1 Oct, as SAP user Software) flipped their drafts the way a B1 client decision does. Transfers into
/// the wash bay (template 91) wait at that same stage, with the same approver listed, so the same
/// PATCH decides them. After an approval the draft, not the request, says whether it can be added:
/// SAP once marked a request Approved and left its draft Pending (credit memo request 86300).
///
/// The PATCH is a durable obligation once it starts — it runs on a token the caller cannot cancel —
/// and a transport failure after it is answered by re-reading the request.
/// </remarks>
public sealed class DecideSapTransferApprovalHandler(
    ISAPServiceLayerClient sap,
    ISapApprovalLookups lookups,
    ISapTransferApprovalAccess access,
    ICreditNoteApprovalStageScope stageScope,
    IIdempotencyRequestStore idempotencyRequestStore,
    IAuditService auditService,
    IOptions<SAPSettings> sapSettings,
    ILogger<DecideSapTransferApprovalHandler> logger)
    : IRequestHandler<DecideSapTransferApprovalCommand, ErrorOr<SapTransferApprovalDecisionResultDto>>
{
    private const string IdempotencyScope = "sap-transfer-approval-decision";

    public async Task<ErrorOr<SapTransferApprovalDecisionResultDto>> Handle(
        DecideSapTransferApprovalCommand command,
        CancellationToken cancellationToken)
    {
        var settings = sapSettings.Value;
        if (!settings.Enabled)
        {
            return Errors.SapTransferApproval.SapDisabled;
        }

        var approving = string.Equals(command.Decision, ApprovalDecisionValues.Approved, StringComparison.OrdinalIgnoreCase);
        var rejecting = string.Equals(command.Decision, ApprovalDecisionValues.NotApproved, StringComparison.OrdinalIgnoreCase);
        if (!approving && !rejecting)
        {
            return Errors.CreditNoteApproval.InvalidDecision(command.Decision);
        }

        var decision = approving ? ApprovalDecisionValues.Approved : ApprovalDecisionValues.NotApproved;
        var sapDecision = approving ? SapApprovalDecisions.Approved : SapApprovalDecisions.NotApproved;
        var approverUserCode = lookups.ServiceApproverUserCode;

        long? idempotencyRequestId = null;
        var release = false;

        async Task<ErrorOr<SapTransferApprovalDecisionResultDto>?> TryClaimAsync(string key, int? stageCode)
        {
            var acquired = await idempotencyRequestStore.TryAcquireAsync<SapTransferApprovalDecisionResultDto>(
                IdempotencyScope,
                key,
                new { command.Code, StageCode = stageCode, command.UserId, Decision = decision, command.Remarks },
                cancellationToken);

            switch (acquired.Outcome)
            {
                case IdempotencyAcquireOutcome.ReplayAvailable when acquired.Response is not null:
                    return (ErrorOr<SapTransferApprovalDecisionResultDto>)acquired.Response;
                case IdempotencyAcquireOutcome.InProgress:
                    return (ErrorOr<SapTransferApprovalDecisionResultDto>)Errors.SapTransferApproval.DecisionInProgress;
                case IdempotencyAcquireOutcome.RequestMismatch:
                    return (ErrorOr<SapTransferApprovalDecisionResultDto>)Errors.Idempotency.RequestMismatch("transfer approval decision");
                default:
                    idempotencyRequestId = acquired.RequestId;
                    release = true;
                    return null;
            }
        }

        try
        {
            var caller = await access.ResolveAsync(command.UserId, cancellationToken);
            if (caller.IsError)
            {
                return caller.Errors;
            }

            var scope = await stageScope.ResolveAsync(command.UserId, cancellationToken);
            if (scope.IsError)
            {
                return scope.Errors;
            }

            var clientRequestId = string.IsNullOrWhiteSpace(command.ClientRequestId) ? null : command.ClientRequestId.Trim();
            if (clientRequestId is not null)
            {
                var claimed = await TryClaimAsync($"{command.Code}:{decision.ToLowerInvariant()}:{command.UserId}:{clientRequestId}", null);
                if (claimed is not null)
                {
                    return claimed.Value;
                }
            }

            var request = await sap.GetApprovalRequestAsync(command.Code, cancellationToken);
            if (request is null || !SapTransferDecisionRules.IsTransferRequest(request))
            {
                return Errors.SapTransferApproval.RequestNotFound(command.Code);
            }

            // Before any state is described: a request outside the caller's stages is not theirs to learn
            // anything about, pending or not.
            if (!scope.Value.Admits(request.CurrentStage))
            {
                return Errors.CreditNoteApproval.OutsideStageScope(command.Code, scope.Value.Describe());
            }

            if (!SapTransferDecisionRules.IsPending(request))
            {
                return Errors.CreditNoteApproval.NotPending(SapApprovalRequestStatuses.ToDisplay(request.Status));
            }

            if (request.CurrentStage is not int stageCode)
            {
                return Errors.CreditNoteApproval.NoCurrentStage;
            }

            // The draft is what moves stock, so its warehouses decide whether a depot or shop user may
            // touch it — the same rule as reading it in the drawer.
            var draft = request.DraftEntry is int entry && entry > 0
                ? await sap.GetStockTransferDraftAsync(entry, cancellationToken)
                : null;
            if (draft is null || !SapTransferApprovalProjection.IsHeldDraft(draft) || !SapTransferApprovalProjection.IsOpen(draft))
            {
                return Errors.SapTransferApproval.DraftGone(command.Code);
            }

            if (!caller.Value.AdmitsDraft(draft))
            {
                return SapTransferApprovalProjection.OutsideScope(caller.Value, draft);
            }

            var stage = await lookups.GetStageAsync(stageCode, cancellationToken);
            var stageName = string.IsNullOrWhiteSpace(stage?.Name) ? $"#{stageCode}" : stage!.Name!;

            var approver = await lookups.GetServiceApproverAsync(cancellationToken);
            if (approver is null)
            {
                return Errors.CreditNoteApproval.ApproverUnknown(approverUserCode);
            }

            if (!CreditNoteApprovalProjection.ServiceApproverListed(stage, approver))
            {
                return Errors.CreditNoteApproval.ApproverNotOnStage(stageName, approverUserCode);
            }

            if (CreditNoteApprovalProjection.ServiceApproverAlreadyDecided(request, approver))
            {
                return Errors.CreditNoteApproval.AlreadyDecided(stageName);
            }

            if (settings.UsesDedicatedApprovalApprover && settings.ResolveApprovalApproverPassword() is null)
            {
                return Errors.CreditNoteApproval.ApproverPasswordMissing(approverUserCode);
            }

            if (clientRequestId is null)
            {
                var claimed = await TryClaimAsync($"{command.Code}:{stageCode}:{command.UserId}:{decision.ToLowerInvariant()}", stageCode);
                if (claimed is not null)
                {
                    return claimed.Value;
                }
            }

            var remarks = DecideCreditNoteApprovalHandler.ComposeRemarks(decision, command.Username, command.Remarks);

            // The last safe abort: nothing has reached SAP yet.
            cancellationToken.ThrowIfCancellationRequested();

            SAPApprovalRequest? after;
            try
            {
                await sap.SubmitApprovalDecisionAsync(
                    command.Code,
                    settings.ResolveNamedApprovalApprover(),
                    settings.ResolveApprovalApproverPassword(),
                    sapDecision,
                    remarks,
                    CancellationToken.None);
                after = await TryReadBackAsync(command.Code);
            }
            catch (SapRequestRejectedException rejected)
            {
                logger.LogWarning(rejected, "SAP refused the {Decision} on transfer approval request {Code}", decision, command.Code);
                await TryAuditAsync(approving, command, draft, stageName, false, $"SAP refused: {rejected.SapMessage}", remarks);
                return Errors.CreditNoteApproval.SapRejected(rejected.SapMessage);
            }
            catch (Exception exception)
            {
                // The PATCH may or may not have landed. SAP knows; ask it before saying anything.
                after = await TryReadBackAsync(command.Code);
                if (after is null || !CreditNoteApprovalProjection.ServiceApproverAlreadyDecided(after, approver))
                {
                    logger.LogError(exception, "The {Decision} on transfer approval request {Code} did not get a clear answer from SAP", decision, command.Code);
                    await TryAuditAsync(approving, command, draft, stageName, false, $"No clear answer from SAP: {exception.Message}", remarks);
                    return Errors.CreditNoteApproval.DecisionUncertain;
                }

                logger.LogWarning(exception, "The {Decision} on transfer approval request {Code} landed although the call failed; SAP shows it recorded", decision, command.Code);
            }

            var final = after ?? request;

            // After an approval the draft is the witness: a draft held by two templates stays Pending until
            // both approve, and SAP has once left a draft Pending behind an approved request.
            var draftAfter = approving ? await TryReadDraftAsync(draft.DocEntry) : null;

            var result = Describe(command.Code, draft, decision, approving, final, stageName, draftAfter);
            await TryAuditAsync(approving, command, draft, stageName, true, $"Status now {result.Status}.", remarks);

            if (idempotencyRequestId.HasValue)
            {
                await idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, result, CancellationToken.None);
                release = false;
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.SapTransferApproval.Cancelled;
        }
        catch (SapRequestRejectedException rejected)
        {
            return Errors.CreditNoteApproval.SapRejected(rejected.SapMessage);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not decide SAP transfer approval request {Code}", command.Code);
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
                    logger.LogWarning(exception, "Failed to release the transfer approval decision lock for request {Code}", command.Code);
                }
            }
        }
    }

    /// <remarks>
    /// <paramref name="draftAfter"/> is the draft read back after an approval; null after a rejection, or
    /// when the read failed — the add checks the draft again before it moves anything.
    /// </remarks>
    internal static SapTransferApprovalDecisionResultDto Describe(
        int code,
        SAPStockTransferDraft draft,
        string decision,
        bool approving,
        SAPApprovalRequest after,
        string stageName,
        SAPStockTransferDraft? draftAfter)
    {
        var noun = SapTransferApprovalProjection.KindOf(draft) == SapHeldDraftKind.Request ? "transfer request" : "transfer";
        var status = SapApprovalRequestStatuses.ToDisplay(after.Status);
        var requestPending = SapTransferDecisionRules.IsPending(after);
        var requestApproved = string.Equals(after.Status, SapApprovalRequestStatuses.Approved, StringComparison.OrdinalIgnoreCase);
        var draftApproved = draftAfter is not null && SapTransferApprovalProjection.IsApproved(draftAfter);
        var draftPending = draftAfter is not null
            && string.Equals(draftAfter.AuthorizationStatus, SapStockTransferAuthorizationStatuses.Pending, StringComparison.OrdinalIgnoreCase);
        var canAdd = requestApproved && draftApproved;

        var message = approving switch
        {
            false => $"Rejected. The {noun} will not be added; the person who keyed it sees the decision in SAP.",
            true when canAdd => $"Approved. The {noun} is ready to add — find it under Ready to add.",
            true when requestPending => $"Approved at stage '{stageName}'. SAP is waiting on another stage before the {noun} can be added.",
            true when requestApproved && draftPending => $"Approved. SAP is still waiting on another approval on this {noun} before it can be added.",
            true when requestApproved && draftAfter is null => $"Approved. Reload to see whether the {noun} is ready to add.",
            true when requestApproved => $"SAP recorded the approval but left the draft {SapStockTransferAuthorizationStatuses.ToDisplay(draftAfter!.AuthorizationStatus)}, so the {noun} cannot be added yet.",
            true => $"Approval recorded. SAP now shows the request as {status}."
        };

        return new SapTransferApprovalDecisionResultDto
        {
            Code = code,
            DraftEntry = draft.DocEntry,
            Decision = decision,
            Status = status,
            CanAdd = canAdd,
            StillPending = approving && !canAdd && (requestPending || draftPending),
            Message = message
        };
    }

    private async Task<SAPApprovalRequest?> TryReadBackAsync(int code)
    {
        try
        {
            return await sap.GetApprovalRequestAsync(code, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read transfer approval request {Code} back after the decision", code);
            return null;
        }
    }

    private async Task<SAPStockTransferDraft?> TryReadDraftAsync(int draftEntry)
    {
        try
        {
            return await sap.GetStockTransferDraftAsync(draftEntry, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read stock transfer draft {DraftEntry} back after the decision", draftEntry);
            return null;
        }
    }

    private async Task TryAuditAsync(
        bool approving,
        DecideSapTransferApprovalCommand command,
        SAPStockTransferDraft draft,
        string stageName,
        bool success,
        string outcome,
        string remarks)
    {
        try
        {
            await auditService.LogAsync(
                approving ? AuditActions.ApproveSapTransfer : AuditActions.RejectSapTransfer,
                "SapApprovalRequest",
                command.Code.ToString(),
                $"{command.Decision} SAP approval request {command.Code} (draft entry {draft.DocEntry}, {draft.FromWarehouse} → {draft.ToWarehouse}) "
                + $"at stage '{stageName}' as SAP user {lookups.ServiceApproverUserCode}. {outcome} Remarks: {remarks}",
                success,
                success ? null : outcome);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to audit the decision on transfer approval request {Code}", command.Code);
        }
    }
}
