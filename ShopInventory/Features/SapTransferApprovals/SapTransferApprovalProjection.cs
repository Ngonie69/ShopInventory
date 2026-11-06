using ShopInventory.DTOs;
using ShopInventory.Features.CreditNoteApprovals;
using ShopInventory.Models;

namespace ShopInventory.Features.SapTransferApprovals;

/// <summary>
/// Shapes a held transfer draft and its approval requests into what the page shows, and decides whether
/// it can be added. One place, so the list, the detail and the add never disagree about a row.
/// </summary>
internal static class SapTransferApprovalProjection
{
    /// <summary>
    /// What a batch or serial pick may fall short of the line by and still count as complete: SAP keeps
    /// six decimals, and a pick summed from several batches can land a rounding step under the line.
    /// </summary>
    private const decimal PickTolerance = 0.000001m;

    public static bool IsTransferDraft(SAPStockTransferDraft draft) =>
        string.Equals(draft.DocObjectCode, SapDocObjectCodes.StockTransfer, StringComparison.Ordinal)
        || string.Equals(draft.DocObjectCode, "oStockTransfer", StringComparison.OrdinalIgnoreCase);

    public static bool IsOpen(SAPStockTransferDraft? draft) =>
        draft is not null
        && (string.IsNullOrWhiteSpace(draft.DocumentStatus)
            || string.Equals(draft.DocumentStatus, SapDocumentStatuses.Open, StringComparison.OrdinalIgnoreCase));

    public static bool IsApproved(SAPStockTransferDraft draft) =>
        string.Equals(draft.AuthorizationStatus, SapStockTransferAuthorizationStatuses.Approved, StringComparison.OrdinalIgnoreCase);

    /// <summary>The newest request holding the draft — the one the B1 client shows.</summary>
    public static SAPApprovalRequest? Primary(IEnumerable<SAPApprovalRequest> requests) =>
        requests.OrderByDescending(request => request.Code).FirstOrDefault();

    /// <summary>
    /// A request on the draft that has not been approved — still pending, or refused — which SAP's own
    /// draft state should already reflect. Null when every request is approved or there is none.
    /// </summary>
    public static SAPApprovalRequest? Unsettled(IEnumerable<SAPApprovalRequest> requests) =>
        requests.FirstOrDefault(request =>
            !string.Equals(request.Status, SapApprovalRequestStatuses.Approved, StringComparison.OrdinalIgnoreCase)
            && !SapApprovalRequestStatuses.IsGenerated(request.Status));

    /// <summary>Every warehouse the draft takes stock out of: the header's and each line's own.</summary>
    public static IReadOnlyList<string> SourceWarehouses(SAPStockTransferDraft draft) =>
        new[] { draft.FromWarehouse }
            .Concat(draft.StockTransferLines?.Select(line => line.FromWarehouseCode) ?? [])
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The transfer requests the draft's lines were copied from in the B1 client.</summary>
    public static IReadOnlyList<int> LinkedRequestEntries(SAPStockTransferDraft draft) =>
        (draft.StockTransferLines ?? [])
            .Where(line => SapStockTransferBaseTypes.IsTransferRequest(line.BaseType) && line.BaseEntry is > 0)
            .Select(line => line.BaseEntry!.Value)
            .Distinct()
            .Order()
            .ToList();

    public static SapTransferApprovalListItemDto ToListItem(
        SAPStockTransferDraft draft,
        IReadOnlyCollection<SAPApprovalRequest> requests,
        SAPUser? originator,
        SAPApprovalTemplate? template,
        SAPApprovalStage? stage,
        SapTransferApprovalCaller caller)
    {
        var primary = Primary(requests);
        var (canAdd, note) = Flags(draft, requests, caller);

        return new SapTransferApprovalListItemDto
        {
            DraftEntry = draft.DocEntry,
            DraftNum = draft.DocNum,
            DocDate = CreditNoteApprovalProjection.ParseSapDate(draft.DocDate),
            DueDate = CreditNoteApprovalProjection.ParseSapDate(draft.DueDate),
            CreatedDate = CreditNoteApprovalProjection.ParseSapDate(draft.CreationDate),
            FromWarehouse = draft.FromWarehouse,
            ToWarehouse = draft.ToWarehouse,
            Comments = draft.Comments,
            DraftStatus = SapStockTransferAuthorizationStatuses.ToDisplay(draft.AuthorizationStatus),
            HasAttachment = draft.AttachmentEntry is > 0,
            RequestCode = primary?.Code,
            RequestCodes = requests.Select(request => request.Code).Distinct().OrderByDescending(code => code).ToList(),
            RequestStatus = primary is null ? null : SapApprovalRequestStatuses.ToDisplay(primary.Status),
            RequestRemarks = primary?.Remarks,
            RequestedDate = CreditNoteApprovalProjection.ParseSapDate(primary?.CreationDate),
            RequestedTime = primary?.CreationTime,
            TemplateCode = primary?.ApprovalTemplatesID,
            TemplateName = template?.Name,
            StageCode = primary?.CurrentStage,
            StageName = stage?.Name,
            OriginatorId = primary?.OriginatorID,
            OriginatorUserCode = originator?.UserCode,
            OriginatorName = originator?.UserName,
            CanAdd = canAdd,
            StatusNote = note
        };
    }

    /// <summary>Whether the draft can be added as far as its header and requests say.</summary>
    public static (bool CanAdd, string? Note) Flags(
        SAPStockTransferDraft draft,
        IEnumerable<SAPApprovalRequest> requests,
        SapTransferApprovalCaller caller)
    {
        if (!IsOpen(draft))
        {
            return (false, "This draft has already been added, or closed, in SAP.");
        }

        if (!IsApproved(draft))
        {
            var state = SapStockTransferAuthorizationStatuses.ToDisplay(draft.AuthorizationStatus);
            return (false, $"SAP has this draft as {(string.IsNullOrWhiteSpace(state) ? "not approved" : state)}, so it cannot be added.");
        }

        if (Unsettled(requests) is { } unsettled)
        {
            return (false, $"SAP approval request {unsettled.Code} on this transfer is still {SapApprovalRequestStatuses.ToDisplay(unsettled.Status)}.");
        }

        var outside = SourceWarehouses(draft).FirstOrDefault(code => !caller.Admits(code));
        if (outside is not null)
        {
            return (false, $"This transfer takes stock out of {outside}, which is not one of your warehouses.");
        }

        return (true, null);
    }

    /// <summary>
    /// The draft's lines, each with whether its batch or serial pick is complete, and a sentence for every
    /// line that is not.
    /// </summary>
    /// <param name="draft">The draft, read by key so its lines carry their picks.</param>
    /// <param name="management">SAP's item flags; null when they could not be read, which checks nothing.</param>
    public static (List<SapTransferDraftLineDto> Lines, List<string> MissingPicks) Lines(
        SAPStockTransferDraft draft,
        IReadOnlyDictionary<string, SapItemManagement>? management)
    {
        var lines = new List<SapTransferDraftLineDto>();
        var missing = new List<string>();

        foreach (var line in draft.StockTransferLines ?? [])
        {
            var flags = management is not null && line.ItemCode is { } code && management.TryGetValue(code, out var found)
                ? found
                : null;
            var batches = line.BatchNumbers ?? [];
            var serialCount = line.SerialNumbers?.Count ?? 0;

            var shortfall = PickShortfall(line, flags);
            if (shortfall is not null)
            {
                missing.Add(shortfall);
            }

            lines.Add(new SapTransferDraftLineDto
            {
                LineNum = line.LineNum,
                ItemCode = line.ItemCode,
                ItemDescription = line.ItemDescription,
                Quantity = line.Quantity,
                UoMCode = line.UoMCode,
                FromWarehouse = line.FromWarehouseCode ?? draft.FromWarehouse,
                ToWarehouse = line.WarehouseCode ?? draft.ToWarehouse,
                ManagesBatches = management is null ? null : flags?.ManagesBatches ?? false,
                ManagesSerials = management is null ? null : flags?.ManagesSerials ?? false,
                Batches = batches.Select(batch => new SapTransferDraftBatchDto
                {
                    BatchNumber = batch.BatchNumber,
                    Quantity = batch.Quantity
                }).ToList(),
                SerialCount = serialCount,
                SelectionMissing = shortfall is not null,
                BaseRequestDocEntry = SapStockTransferBaseTypes.IsTransferRequest(line.BaseType) ? line.BaseEntry : null
            });
        }

        return (lines, missing);
    }

    /// <summary>
    /// Why a batch- or serial-managed line's pick does not cover it, or null when it does.
    /// </summary>
    /// <remarks>
    /// The pick is compared with the smaller of the line's quantity and its inventory quantity, because
    /// SAP counts picks in the inventory unit and the line in its own. Taking the smaller can only miss
    /// a shortfall, never invent one, and SAP refuses whatever this misses anyway.
    /// </remarks>
    private static string? PickShortfall(SAPStockTransferDraftLine line, SapItemManagement? flags)
    {
        if (flags is null || line.Quantity <= 0)
        {
            return null;
        }

        var required = line.InventoryQuantity is > 0 && line.InventoryQuantity < line.Quantity
            ? line.InventoryQuantity.Value
            : line.Quantity;
        // Named by item and quantity, not LineNum: SAP's LineNum need not match the row number the B1
        // client shows once a line has been deleted, and the quantity tells two lines of one item apart.
        var quantity = string.IsNullOrWhiteSpace(line.UoMCode)
            ? $"{line.Quantity:0.######}"
            : $"{line.Quantity:0.######} {line.UoMCode}";
        var label = $"{line.ItemCode} ({quantity})";

        if (flags.ManagesBatches)
        {
            var picked = line.BatchNumbers?.Sum(batch => batch.Quantity) ?? 0m;
            if (picked + PickTolerance < required)
            {
                return picked <= 0
                    ? $"{label} has no batch picked"
                    : $"{label} has batches picked for {picked:0.######} of {required:0.######}";
            }
        }

        if (flags.ManagesSerials)
        {
            var picked = line.SerialNumbers?.Count ?? 0;
            if (picked + PickTolerance < required)
            {
                return picked == 0
                    ? $"{label} has no serial numbers picked"
                    : $"{label} has {picked} of {required:0.######} serial numbers picked";
            }
        }

        return null;
    }

    /// <summary>Whether SAP refused an add because a batch or serial pick was incomplete.</summary>
    public static bool IsSelectionRefusal(string? sapMessage) =>
        !string.IsNullOrWhiteSpace(sapMessage)
        && (sapMessage.Contains("batch/serial", StringComparison.OrdinalIgnoreCase)
            || sapMessage.Contains("selection of batch", StringComparison.OrdinalIgnoreCase)
            || sapMessage.Contains("selection of serial", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether <paramref name="transfer"/> carries exactly the draft's lines — item, quantity and both
    /// warehouses, as a multiset — which is how the transfer an add produced is recognised when SAP's
    /// answer does not name it.
    /// </summary>
    /// <remarks>Never DocNum: drafts and transfers number from different series.</remarks>
    public static bool CarriesSameLines(SAPStockTransferDraft draft, InventoryTransfer transfer)
    {
        static string Key(string? item, decimal quantity, string? from, string? to) =>
            $"{item?.Trim().ToUpperInvariant()}|{quantity:0.######}|{from?.Trim().ToUpperInvariant()}|{to?.Trim().ToUpperInvariant()}";

        var expected = (draft.StockTransferLines ?? [])
            .Select(line => Key(line.ItemCode, line.Quantity, line.FromWarehouseCode ?? draft.FromWarehouse, line.WarehouseCode ?? draft.ToWarehouse))
            .Order(StringComparer.Ordinal)
            .ToList();
        var actual = (transfer.StockTransferLines ?? [])
            .Select(line => Key(line.ItemCode, line.Quantity, line.FromWarehouseCode ?? transfer.FromWarehouse, line.WarehouseCode ?? transfer.ToWarehouse))
            .Order(StringComparer.Ordinal)
            .ToList();

        return expected.Count > 0 && expected.SequenceEqual(actual, StringComparer.Ordinal);
    }

    public static SapTransferLinkedRequestDto ToLinkedRequest(int docEntry, InventoryTransferRequest? request) => new()
    {
        DocEntry = docEntry,
        DocNum = request?.DocNum,
        Status = request is null ? null : IsClosed(request) ? "Closed" : "Open",
        FromWarehouse = request?.FromWarehouse,
        ToWarehouse = request?.ToWarehouse
    };

    public static bool IsClosed(InventoryTransferRequest request) =>
        string.Equals(request.DocumentStatus, SapDocumentStatuses.Closed, StringComparison.OrdinalIgnoreCase)
        || string.Equals(request.DocumentStatus, "Closed", StringComparison.OrdinalIgnoreCase);
}
