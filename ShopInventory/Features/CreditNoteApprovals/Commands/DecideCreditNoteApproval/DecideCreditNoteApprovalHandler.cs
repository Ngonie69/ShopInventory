using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNoteApprovals.Commands.DecideCreditNoteApproval;

/// <summary>
/// Records an approve or reject on a SAP approval request. The decision goes to SAP as the service
/// approver — the one SAP user the stages list — and the remarks name the person who clicked, so
/// SAP's own record still says who decided.
/// </summary>
/// <remarks>
/// The PATCH is a durable obligation once it starts: it runs on a token the caller cannot cancel, and a
/// transport failure after it is answered by re-reading the request rather than by guessing. Refusals
/// SAP can express are checked first — not pending, no stage, the service approver not listed or
/// already decided — so the person sees a sentence instead of SAP's error text.
///
/// The idempotency claim is taken before anything is read when the caller sends a client request id:
/// a retry of a call that timed out after SAP recorded the decision then replays the first answer
/// instead of being refused as "not pending". Without an id the claim is per stage and person, which
/// only guards a double click.
/// </remarks>
public sealed class DecideCreditNoteApprovalHandler(
    ISAPServiceLayerClient sap,
    ISapApprovalLookups lookups,
    IIdempotencyRequestStore idempotencyRequestStore,
    IAuditService auditService,
    IOptions<SAPSettings> sapSettings,
    ILogger<DecideCreditNoteApprovalHandler> logger)
    : IRequestHandler<DecideCreditNoteApprovalCommand, ErrorOr<CreditNoteApprovalDecisionResultDto>>
{
    /// <summary>
    /// What SAP's remark column holds. Measured against KEFALOS_TEST_3 on 2026-09-02: 200 is accepted,
    /// 201 is not, and SAP refuses the whole decision rather than truncating.
    /// </summary>
    internal const int SapRemarksLength = 200;

    private const string IdempotencyScope = "credit-note-approval-decision";

    public async Task<ErrorOr<CreditNoteApprovalDecisionResultDto>> Handle(
        DecideCreditNoteApprovalCommand command,
        CancellationToken cancellationToken)
    {
        var settings = sapSettings.Value;
        if (!settings.Enabled)
        {
            return Errors.CreditNoteApproval.SapDisabled;
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

        async Task<ErrorOr<CreditNoteApprovalDecisionResultDto>?> TryClaimAsync(string key, int? stageCode)
        {
            var acquired = await idempotencyRequestStore.TryAcquireAsync<CreditNoteApprovalDecisionResultDto>(
                IdempotencyScope,
                key,
                new { command.Code, StageCode = stageCode, command.UserId, Decision = decision, command.Remarks },
                cancellationToken);

            switch (acquired.Outcome)
            {
                case IdempotencyAcquireOutcome.ReplayAvailable when acquired.Response is not null:
                    return (ErrorOr<CreditNoteApprovalDecisionResultDto>)acquired.Response;
                case IdempotencyAcquireOutcome.InProgress:
                    return (ErrorOr<CreditNoteApprovalDecisionResultDto>)Errors.CreditNoteApproval.DecisionInProgress;
                case IdempotencyAcquireOutcome.RequestMismatch:
                    return (ErrorOr<CreditNoteApprovalDecisionResultDto>)Errors.Idempotency.RequestMismatch("credit note approval decision");
                default:
                    idempotencyRequestId = acquired.RequestId;
                    release = true;
                    return null;
            }
        }

        try
        {
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
            if (request is null || !string.Equals(request.ObjectType, SapObjectTypes.CreditNote, StringComparison.Ordinal))
            {
                return Errors.CreditNoteApproval.NotFound(command.Code);
            }

            if (!string.Equals(request.Status, SapApprovalRequestStatuses.Pending, StringComparison.OrdinalIgnoreCase))
            {
                return Errors.CreditNoteApproval.NotPending(SapApprovalRequestStatuses.ToDisplay(request.Status));
            }

            if (request.CurrentStage is not int stageCode)
            {
                return Errors.CreditNoteApproval.NoCurrentStage;
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

            // SAP will not take a named approver without that approver's password, and its refusal
            // reads as bad credentials rather than as missing configuration. Say so here instead.
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

            var remarks = ComposeRemarks(decision, command.Username, command.Remarks);

            // The last safe abort: nothing has reached SAP yet. From here the decision is a durable
            // obligation and runs on a token the caller cannot cancel.
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
                logger.LogWarning(rejected, "SAP refused the {Decision} on approval request {Code}", decision, command.Code);
                await TryAuditAsync(approving, command, stageName, false, $"SAP refused: {rejected.SapMessage}", remarks);
                return Errors.CreditNoteApproval.SapRejected(rejected.SapMessage);
            }
            catch (Exception exception)
            {
                // The PATCH may or may not have landed. SAP knows; ask it before saying anything.
                after = await TryReadBackAsync(command.Code);
                if (after is null || !CreditNoteApprovalProjection.ServiceApproverAlreadyDecided(after, approver))
                {
                    logger.LogError(exception, "The {Decision} on approval request {Code} did not get a clear answer from SAP", decision, command.Code);
                    await TryAuditAsync(approving, command, stageName, false, $"No clear answer from SAP: {exception.Message}", remarks);
                    return Errors.CreditNoteApproval.DecisionUncertain;
                }

                logger.LogWarning(exception, "The {Decision} on approval request {Code} landed although the call failed; SAP shows it recorded", decision, command.Code);
            }

            var result = Describe(command.Code, decision, approving, after ?? request, stageName);
            await TryAuditAsync(approving, command, stageName, true, $"Status now {result.Status}.", remarks);

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
            logger.LogError(exception, "Could not decide SAP approval request {Code}", command.Code);
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
                    logger.LogWarning(exception, "Failed to release the credit note approval decision lock for request {Code}", command.Code);
                }
            }
        }
    }

    /// <summary>
    /// "Approved in ShopInventory by ngoni: looks right", cut to SAP's column with the person's name
    /// kept and the free text truncated — the audit value is who, not how much they wrote.
    /// </summary>
    internal static string ComposeRemarks(string decision, string username, string? remarks)
    {
        var prefix = $"{decision} in ShopInventory by {username}";
        var text = string.IsNullOrWhiteSpace(remarks) ? prefix : $"{prefix}: {remarks.Trim()}";
        return text.Length <= SapRemarksLength ? text : text[..SapRemarksLength];
    }

    private static CreditNoteApprovalDecisionResultDto Describe(
        int code,
        string decision,
        bool approving,
        SAPApprovalRequest after,
        string stageName)
    {
        var status = SapApprovalRequestStatuses.ToDisplay(after.Status);
        var stillPending = string.Equals(after.Status, SapApprovalRequestStatuses.Pending, StringComparison.OrdinalIgnoreCase);
        var canAdd = string.Equals(after.Status, SapApprovalRequestStatuses.Approved, StringComparison.OrdinalIgnoreCase);

        var message = (approving, stillPending, canAdd) switch
        {
            (true, _, true) => "Approval complete. The credit note can now be added.",
            (true, true, _) => $"Approved at stage '{stageName}'. SAP is waiting on another stage before the credit note can be added.",
            (true, _, _) => $"Approval recorded. SAP now shows the request as {status}.",
            (false, _, _) => "Rejected. The originator will see the decision in SAP."
        };

        return new CreditNoteApprovalDecisionResultDto
        {
            Code = code,
            Decision = decision,
            Status = status,
            CanAdd = canAdd,
            StillPending = stillPending,
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
            logger.LogWarning(exception, "Could not read approval request {Code} back after the decision", code);
            return null;
        }
    }

    private async Task TryAuditAsync(
        bool approving,
        DecideCreditNoteApprovalCommand command,
        string stageName,
        bool success,
        string outcome,
        string remarks)
    {
        try
        {
            await auditService.LogAsync(
                approving ? AuditActions.ApproveSapCreditNote : AuditActions.RejectSapCreditNote,
                "SapApprovalRequest",
                command.Code.ToString(),
                $"{command.Decision} SAP approval request {command.Code} at stage '{stageName}' as SAP user {lookups.ServiceApproverUserCode}. {outcome} Remarks: {remarks}",
                success,
                success ? null : outcome);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to audit the decision on approval request {Code}", command.Code);
        }
    }
}
