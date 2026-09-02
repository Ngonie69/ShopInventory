using System.Globalization;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Features.CreditNoteApprovals;

/// <summary>
/// Shapes a SAP approval request and the draft it holds into what the pages show, and decides what the
/// app may do with it next. One place, because the list and the detail must never disagree about
/// whether a row can be decided or added.
/// </summary>
internal static class CreditNoteApprovalProjection
{
    public static CreditNoteApprovalListItemDto ToListItem(
        SAPApprovalRequest request,
        SAPCreditNote? draft,
        SAPUser? originator,
        SAPApprovalTemplate? template,
        SAPApprovalStage? stage,
        SAPUser? serviceApprover,
        string serviceApproverUserCode,
        bool serviceApproverAlreadyDecided)
    {
        var (canDecide, canAdd, note) = Flags(request, draft, stage, serviceApprover, serviceApproverUserCode, serviceApproverAlreadyDecided);

        return new CreditNoteApprovalListItemDto
        {
            Code = request.Code,
            Status = SapApprovalRequestStatuses.ToDisplay(request.Status),
            Remarks = request.Remarks,
            CreatedDate = ParseSapDate(request.CreationDate),
            CreatedTime = request.CreationTime,
            TemplateCode = request.ApprovalTemplatesID,
            TemplateName = template?.Name,
            StageCode = request.CurrentStage,
            StageName = stage?.Name,
            OriginatorId = request.OriginatorID,
            OriginatorUserCode = originator?.UserCode,
            OriginatorName = originator?.UserName,
            DraftEntry = request.DraftEntry,
            DraftNum = draft?.DocNum,
            CardCode = draft?.CardCode,
            CardName = draft?.CardName,
            DocDate = ParseSapDate(draft?.DocDate),
            DocTotal = draft?.DocTotal ?? 0m,
            VatSum = draft?.VatSum ?? 0m,
            DocCurrency = draft?.DocCurrency,
            NumAtCard = draft?.NumAtCard,
            Comments = draft?.Comments,
            DraftAuthorizationStatus = draft is null ? null : SapEnumNames.StripPrefix(draft.AuthorizationStatus, "das"),
            DraftIsOpen = IsOpen(draft),
            HasAttachment = draft?.AttachmentEntry is > 0,
            CreditNoteDocEntry = request.ObjectEntry,
            CanDecide = canDecide,
            CanAdd = canAdd,
            StatusNote = note
        };
    }

    public static (bool CanDecide, bool CanAdd, string? StatusNote) Flags(
        SAPApprovalRequest request,
        SAPCreditNote? draft,
        SAPApprovalStage? stage,
        SAPUser? serviceApprover,
        string serviceApproverUserCode,
        bool serviceApproverAlreadyDecided)
    {
        var status = request.Status ?? string.Empty;

        if (string.Equals(status, SapApprovalRequestStatuses.Pending, StringComparison.OrdinalIgnoreCase))
        {
            if (request.CurrentStage is null)
            {
                return (false, false, "SAP has not assigned this request a stage yet.");
            }

            if (stage is null)
            {
                return (false, false, $"SAP stage {request.CurrentStage} could not be read.");
            }

            if (serviceApprover is null)
            {
                return (false, false, $"SAP has no user '{serviceApproverUserCode}' to decide as. Check SAP:ApprovalApproverUsername.");
            }

            if (!ServiceApproverListed(stage, serviceApprover))
            {
                return (false, false, $"SAP stage '{stage.Name}' does not list {serviceApproverUserCode} as an approver.");
            }

            if (serviceApproverAlreadyDecided)
            {
                return (false, false, $"{serviceApproverUserCode} has already decided stage '{stage.Name}'; SAP is waiting on another approver.");
            }

            return (true, false, null);
        }

        if (string.Equals(status, SapApprovalRequestStatuses.Approved, StringComparison.OrdinalIgnoreCase))
        {
            if (draft is null)
            {
                return (false, false, "The draft behind this request no longer exists in SAP.");
            }

            if (!IsOpen(draft))
            {
                return (false, false, "The draft is closed or cancelled in SAP.");
            }

            if (!string.IsNullOrWhiteSpace(draft.AuthorizationStatus)
                && !string.Equals(draft.AuthorizationStatus, SapDocumentAuthorizationStatuses.Approved, StringComparison.OrdinalIgnoreCase))
            {
                var draftState = SapEnumNames.StripPrefix(draft.AuthorizationStatus, "das");
                return (false, false, $"The draft's own approval state is {draftState}, not Approved; it may have been changed in SAP since.");
            }

            return (false, true, null);
        }

        if (SapApprovalRequestStatuses.IsGenerated(status))
        {
            return (false, false, request.ObjectEntry is int docEntry
                ? $"Added as credit note DocEntry {docEntry}."
                : "Already added.");
        }

        if (string.Equals(status, SapApprovalRequestStatuses.NotApproved, StringComparison.OrdinalIgnoreCase))
        {
            return (false, false, "Rejected.");
        }

        if (string.Equals(status, SapApprovalRequestStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            return (false, false, "Cancelled in SAP.");
        }

        return (false, false, null);
    }

    public static bool ServiceApproverListed(SAPApprovalStage? stage, SAPUser? serviceApprover) =>
        stage?.ApprovalStageApprovers is not null
        && serviceApprover is not null
        && stage.ApprovalStageApprovers.Any(approver => approver.UserID == serviceApprover.InternalKey);

    /// <summary>The service approver already has a non-pending line on the request's current stage.</summary>
    public static bool ServiceApproverAlreadyDecided(SAPApprovalRequest request, SAPUser? serviceApprover) =>
        serviceApprover is not null
        && request.ApprovalRequestLines is not null
        && request.ApprovalRequestLines.Any(line =>
            line.UserID == serviceApprover.InternalKey
            && (request.CurrentStage is null || line.StageCode == request.CurrentStage)
            && !string.IsNullOrWhiteSpace(line.Status)
            && !string.Equals(line.Status, SapApprovalDecisions.Pending, StringComparison.OrdinalIgnoreCase));

    public static bool IsOpen(SAPCreditNote? draft) =>
        draft is not null
        && !string.Equals(draft.Cancelled, SapYesNo.Yes, StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrWhiteSpace(draft.DocumentStatus)
            || string.Equals(draft.DocumentStatus, SapDocumentStatuses.Open, StringComparison.OrdinalIgnoreCase));

    public static CreditNoteDraftLineDto ToLine(SAPCreditNoteLine line) => new()
    {
        LineNum = line.LineNum,
        ItemCode = line.ItemCode,
        ItemDescription = line.ItemDescription,
        Quantity = line.Quantity,
        UnitPrice = line.UnitPrice,
        LineTotal = line.LineTotal,
        VatSum = line.VatSum,
        WarehouseCode = line.WarehouseCode,
        TaxCode = line.TaxCode,
        BaseType = line.BaseType,
        BaseEntry = line.BaseEntry,
        BaseLine = line.BaseLine,
        CreditReason = line.CreditReason
    };

    /// <summary>SAP's <c>2026-09-01T00:00:00Z</c> or <c>2026-09-01</c>, as a UTC date; null for anything else.</summary>
    public static DateTime? ParseSapDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
    }
}
