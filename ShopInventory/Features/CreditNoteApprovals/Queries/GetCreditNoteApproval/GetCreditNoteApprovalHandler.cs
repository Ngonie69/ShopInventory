using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNoteApprovals.Queries.GetCreditNoteApproval;

public sealed class GetCreditNoteApprovalHandler(
    ISAPServiceLayerClient sap,
    ISapApprovalLookups lookups,
    IOptions<SAPSettings> sapSettings,
    ILogger<GetCreditNoteApprovalHandler> logger)
    : IRequestHandler<GetCreditNoteApprovalQuery, ErrorOr<CreditNoteApprovalDetailDto>>
{
    public async Task<ErrorOr<CreditNoteApprovalDetailDto>> Handle(
        GetCreditNoteApprovalQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
        {
            return Errors.CreditNoteApproval.SapDisabled;
        }

        var request = await sap.GetApprovalRequestAsync(query.Code, cancellationToken);
        if (request is null || !string.Equals(request.ObjectType, SapObjectTypes.CreditNote, StringComparison.Ordinal))
        {
            return Errors.CreditNoteApproval.NotFound(query.Code);
        }

        SAPCreditNote? draft = null;
        if (request.DraftEntry is int draftEntry)
        {
            draft = await sap.GetCreditNoteDraftAsync(draftEntry, cancellationToken);
            if (draft is not null
                && !string.Equals(draft.DocObjectCode, SapDocObjectCodes.CreditNotes, StringComparison.OrdinalIgnoreCase))
            {
                return Errors.CreditNoteApproval.NotACreditNoteDraft(draftEntry);
            }
        }

        var serviceApprover = await TryLookupAsync(() => lookups.GetServiceApproverAsync(cancellationToken), "the service approver");
        var originator = request.OriginatorID is int originatorId
            ? await TryLookupAsync(() => lookups.GetUserAsync(originatorId, cancellationToken), $"SAP user {originatorId}")
            : null;
        var template = request.ApprovalTemplatesID is int templateCode
            ? await TryLookupAsync(() => lookups.GetTemplateAsync(templateCode, cancellationToken), $"approval template {templateCode}")
            : null;
        var stage = request.CurrentStage is int stageCode
            ? await TryLookupAsync(() => lookups.GetStageAsync(stageCode, cancellationToken), $"approval stage {stageCode}")
            : null;

        var alreadyDecided = CreditNoteApprovalProjection.ServiceApproverAlreadyDecided(request, serviceApprover);

        var detail = new CreditNoteApprovalDetailDto
        {
            Request = CreditNoteApprovalProjection.ToListItem(
                request, draft, originator, template, stage, serviceApprover, lookups.ServiceApproverUserCode, alreadyDecided),
            Lines = draft?.DocumentLines?.Select(CreditNoteApprovalProjection.ToLine).ToList() ?? [],
            Decisions = await BuildDecisionsAsync(request, cancellationToken),
            Stage = await BuildStageAsync(request, stage, serviceApprover, alreadyDecided, cancellationToken)
        };

        if (draft?.AttachmentEntry is int attachmentEntry && attachmentEntry > 0)
        {
            try
            {
                var attachment = await sap.GetAttachmentAsync(attachmentEntry, cancellationToken);
                detail.Attachments = attachment?.Attachments2_Lines?
                    .OrderBy(line => line.LineNum)
                    .Select(line => CreditNoteDraftAttachments.ToDto(request.Code, line))
                    .ToList() ?? [];
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The rest of the detail is still good; the page says the files could not be listed.
                logger.LogWarning(exception, "Could not read attachment {AttachmentEntry} for approval request {Code}", attachmentEntry, request.Code);
                detail.AttachmentsUnavailable = true;
                detail.AttachmentsMessage = exception is SapRequestRejectedException rejected ? rejected.SapMessage : exception.Message;
            }
        }

        return detail;
    }

    private async Task<List<CreditNoteApprovalDecisionLineDto>> BuildDecisionsAsync(
        SAPApprovalRequest request,
        CancellationToken cancellationToken)
    {
        var lines = new List<CreditNoteApprovalDecisionLineDto>();
        foreach (var line in request.ApprovalRequestLines ?? [])
        {
            var user = line.UserID is int userId
                ? await TryLookupAsync(() => lookups.GetUserAsync(userId, cancellationToken), $"SAP user {userId}")
                : null;
            var stage = line.StageCode is int stageCode
                ? await TryLookupAsync(() => lookups.GetStageAsync(stageCode, cancellationToken), $"approval stage {stageCode}")
                : null;

            lines.Add(new CreditNoteApprovalDecisionLineDto
            {
                StageCode = line.StageCode,
                StageName = stage?.Name,
                UserId = line.UserID,
                UserCode = user?.UserCode,
                UserName = user?.UserName,
                Status = SapApprovalDecisions.ToDisplay(line.Status),
                Remarks = line.Remarks,
                DecidedDate = CreditNoteApprovalProjection.ParseSapDate(line.UpdateDate),
                DecidedTime = line.UpdateTime
            });
        }

        return lines;
    }

    private async Task<CreditNoteApprovalStageDto?> BuildStageAsync(
        SAPApprovalRequest request,
        SAPApprovalStage? stage,
        SAPUser? serviceApprover,
        bool alreadyDecided,
        CancellationToken cancellationToken)
    {
        if (request.CurrentStage is null)
        {
            return null;
        }

        var approverCodes = new List<string>();
        foreach (var approver in stage?.ApprovalStageApprovers ?? [])
        {
            if (approver.UserID is not int userId)
            {
                continue;
            }

            var user = await TryLookupAsync(() => lookups.GetUserAsync(userId, cancellationToken), $"SAP user {userId}");
            approverCodes.Add(user?.UserCode ?? $"#{userId}");
        }

        return new CreditNoteApprovalStageDto
        {
            Code = request.CurrentStage,
            Name = stage?.Name,
            ApproversRequired = stage?.NoOfApproversRequired,
            ApproverUserCodes = approverCodes,
            ServiceApproverUserCode = lookups.ServiceApproverUserCode,
            ServiceApproverListed = CreditNoteApprovalProjection.ServiceApproverListed(stage, serviceApprover),
            ServiceApproverAlreadyDecided = alreadyDecided
        };
    }

    private async Task<T?> TryLookupAsync<T>(Func<Task<T?>> lookup, string what) where T : class
    {
        try
        {
            return await lookup();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not read {What} from SAP; the approval detail shows the code instead", what);
            return null;
        }
    }
}
