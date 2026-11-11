using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.CreditNoteApprovals;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.SapTransferApprovals.Queries.GetSapTransferApproval;

/// <summary>
/// One held transfer or transfer request: the draft read by key (the only read that carries its batch
/// picks), the approval requests behind it with their approver lines, and for a transfer, which lines
/// lack a batch or serial pick and the transfer request it was copied from.
/// </summary>
public sealed class GetSapTransferApprovalHandler(
    ISAPServiceLayerClient sap,
    ISapApprovalLookups lookups,
    ISapTransferApprovalAccess access,
    IOptions<SAPSettings> sapSettings,
    ILogger<GetSapTransferApprovalHandler> logger)
    : IRequestHandler<GetSapTransferApprovalQuery, ErrorOr<SapTransferApprovalDetailDto>>
{
    public async Task<ErrorOr<SapTransferApprovalDetailDto>> Handle(
        GetSapTransferApprovalQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
        {
            return Errors.SapTransferApproval.SapDisabled;
        }

        var caller = await access.ResolveAsync(query.CallerUserId, cancellationToken);
        if (caller.IsError)
        {
            return caller.Errors;
        }

        SAPStockTransferDraft? draft;
        List<SAPApprovalRequest> requests;
        try
        {
            draft = await sap.GetStockTransferDraftAsync(query.DraftEntry, cancellationToken);
            if (draft is null || !SapTransferApprovalProjection.IsHeldDraft(draft))
            {
                return Errors.SapTransferApproval.NotFound(query.DraftEntry);
            }

            // Before anything else is described: a transfer out of somebody else's warehouse is not
            // theirs to read about, and nor is a request for stock from one.
            if (!caller.Value.Admits(draft.FromWarehouse))
            {
                return Errors.SapTransferApproval.OutsideWarehouseScope(
                    draft.FromWarehouse ?? "an unnamed warehouse", SapTransferApprovalProjection.KindOf(draft));
            }

            requests = await sap.GetStockTransferApprovalRequestsAsync([draft.DocEntry], withLines: true, cancellationToken);
        }
        catch (SapRequestRejectedException rejected)
        {
            return Errors.SapTransferApproval.SapRejected(rejected.SapMessage);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not read stock transfer draft {DraftEntry} from SAP", query.DraftEntry);
            return Errors.SapTransferApproval.SapUnavailable(exception.Message);
        }

        // A request carries no picks — batches are chosen when it is converted — so there is nothing to
        // check and the flags are not read. For a transfer, without the flags nothing can be said about
        // the picks; the page says so and SAP decides at add.
        var isRequest = SapTransferApprovalProjection.IsRequestDraft(draft);
        IReadOnlyDictionary<string, SapItemManagement>? management = null;
        var flagsUnread = false;
        if (!isRequest)
        {
            try
            {
                management = await sap.GetItemManagementAsync(
                    draft.StockTransferLines?.Select(line => line.ItemCode ?? string.Empty).ToList() ?? [],
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not read the batch and serial flags for draft {DraftEntry}", draft.DocEntry);
                flagsUnread = true;
            }
        }

        var primary = SapTransferApprovalProjection.Primary(requests);
        var originator = await LabelAsync(primary?.OriginatorID, (id, token) => lookups.GetUserAsync(id, token), "SAP user", cancellationToken);
        var template = await LabelAsync(primary?.ApprovalTemplatesID, (code, token) => lookups.GetTemplateAsync(code, token), "approval template", cancellationToken);
        var stage = await LabelAsync(primary?.CurrentStage, (code, token) => lookups.GetStageAsync(code, token), "approval stage", cancellationToken);

        var item = SapTransferApprovalProjection.ToListItem(draft, requests, originator, template, stage, caller.Value);
        var (lines, missingPicks) = SapTransferApprovalProjection.Lines(draft, management);

        // The list could only see the header; the detail has every line, so it can say more.
        if (item.CanAdd && missingPicks.Count > 0)
        {
            item.CanAdd = false;
            item.StatusNote = Errors.SapTransferApproval.SelectionMissing(missingPicks).Description;
        }

        return new SapTransferApprovalDetailDto
        {
            Draft = item,
            Lines = lines,
            Approvals = await BuildApprovalsAsync(requests, cancellationToken),
            LinkedRequests = isRequest ? [] : await ReadLinkedRequestsAsync(draft, cancellationToken),
            SelectionUnchecked = flagsUnread
        };
    }

    private async Task<List<SapTransferApprovalRequestDto>> BuildApprovalsAsync(
        IEnumerable<SAPApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        var approvals = new List<SapTransferApprovalRequestDto>();
        foreach (var request in requests.OrderByDescending(request => request.Code))
        {
            var template = await LabelAsync(request.ApprovalTemplatesID, (code, token) => lookups.GetTemplateAsync(code, token), "approval template", cancellationToken);
            var stage = await LabelAsync(request.CurrentStage, (code, token) => lookups.GetStageAsync(code, token), "approval stage", cancellationToken);

            var decisions = new List<SapTransferApprovalDecisionDto>();
            foreach (var line in request.ApprovalRequestLines ?? [])
            {
                var user = await LabelAsync(line.UserID, (id, token) => lookups.GetUserAsync(id, token), "SAP user", cancellationToken);
                var lineStage = await LabelAsync(line.StageCode, (code, token) => lookups.GetStageAsync(code, token), "approval stage", cancellationToken);
                decisions.Add(new SapTransferApprovalDecisionDto
                {
                    StageCode = line.StageCode,
                    StageName = lineStage?.Name,
                    UserId = line.UserID,
                    UserCode = user?.UserCode,
                    UserName = user?.UserName,
                    Status = SapApprovalDecisions.ToDisplay(line.Status),
                    Remarks = line.Remarks,
                    DecidedDate = CreditNoteApprovalProjection.ParseSapDate(line.UpdateDate),
                    DecidedTime = line.UpdateTime
                });
            }

            approvals.Add(new SapTransferApprovalRequestDto
            {
                Code = request.Code,
                Status = SapApprovalRequestStatuses.ToDisplay(request.Status),
                TemplateCode = request.ApprovalTemplatesID,
                TemplateName = template?.Name,
                StageCode = request.CurrentStage,
                StageName = stage?.Name,
                Remarks = request.Remarks,
                CreatedDate = CreditNoteApprovalProjection.ParseSapDate(request.CreationDate),
                CreatedTime = request.CreationTime,
                Decisions = decisions
            });
        }

        return approvals;
    }

    private async Task<List<SapTransferLinkedRequestDto>> ReadLinkedRequestsAsync(
        SAPStockTransferDraft draft,
        CancellationToken cancellationToken)
    {
        var linked = new List<SapTransferLinkedRequestDto>();
        foreach (var docEntry in SapTransferApprovalProjection.LinkedRequestEntries(draft))
        {
            InventoryTransferRequest? request = null;
            try
            {
                request = await sap.GetInventoryTransferRequestByDocEntryAsync(docEntry, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not read transfer request {DocEntry} behind draft {DraftEntry}", docEntry, draft.DocEntry);
            }

            linked.Add(SapTransferApprovalProjection.ToLinkedRequest(docEntry, request));
        }

        return linked;
    }

    private async Task<T?> LabelAsync<T>(
        int? code,
        Func<int, CancellationToken, Task<T?>> lookup,
        string what,
        CancellationToken cancellationToken)
        where T : class
    {
        if (code is not int value)
        {
            return null;
        }

        try
        {
            return await lookup(value, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not read {What} {Code} from SAP; the held transfer shows the code instead", what, value);
            return null;
        }
    }
}
