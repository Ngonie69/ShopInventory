using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers.Commands.ConvertTransferRequest;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.SapTransferApprovals.Commands.AddApprovedSapTransfer;

/// <summary>
/// Adds an approved inventory transfer draft: <c>StockTransferDrafts(n)/SaveDraftToDocument</c>, then the
/// transfer it became is recognised, and any transfer request the draft was copied from is closed.
/// </summary>
/// <remarks>
/// <para>The add moves stock out of the source warehouse and it happens once. The claim is keyed on the
/// draft alone, whoever clicks; the SAP call runs on a token the caller cannot cancel; and a call whose
/// answer was lost is settled by the draft, which SAP closes when it converts — the same witness the
/// credit memo add uses, measured on a transfer draft the B1 client added (<c>bost_Close</c>,
/// <c>sasWithout</c>).</para>
///
/// <para>Who may add mirrors who may convert a transfer request, through the same
/// <see cref="ITransferWarehouseAuthorizer.EnsureCanConvertRequestAsync"/>, asked for every warehouse the
/// draft takes stock out of — its header and each line.</para>
///
/// <para>A draft copied from a transfer request in the B1 client carries the request on its lines
/// (<c>BaseType InventoryTransferRequest</c>). The request is held under the conversion lock while the
/// add runs, refused if this app already converted it (its transfer carries <c>ITR-{DocEntry}</c>), and
/// closed afterwards, so nobody converts it again and moves the stock twice.</para>
/// </remarks>
public sealed class AddApprovedSapTransferHandler(
    ISAPServiceLayerClient sap,
    ITransferWarehouseAuthorizer warehouseAuthorizer,
    IInventoryTransferApprovalService approvalService,
    IUnpostedTillClaims tillClaims,
    IStockValidationService stockValidation,
    IIdempotencyRequestStore idempotencyRequestStore,
    IAuditService auditService,
    IOptions<SAPSettings> sapSettings,
    ILogger<AddApprovedSapTransferHandler> logger)
    : IRequestHandler<AddApprovedSapTransferCommand, ErrorOr<AddApprovedSapTransferResultDto>>
{
    internal const string IdempotencyScope = "sap-transfer-draft-add";

    public async Task<ErrorOr<AddApprovedSapTransferResultDto>> Handle(
        AddApprovedSapTransferCommand command,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
        {
            return Errors.SapTransferApproval.SapDisabled;
        }

        long? idempotencyRequestId = null;
        var release = false;
        var requestLocks = new List<long>();
        try
        {
            var acquired = await idempotencyRequestStore.TryAcquireAsync<AddApprovedSapTransferResultDto>(
                IdempotencyScope,
                command.DraftEntry.ToString(),
                new { command.DraftEntry },
                cancellationToken);

            switch (acquired.Outcome)
            {
                // The draft is closed by now, so the warehouses the first answer recorded decide.
                case IdempotencyAcquireOutcome.ReplayAvailable when acquired.Response is not null:
                    var replayCheck = await EnsureMayAddAsync(command.UserId, [acquired.Response.FromWarehouse], cancellationToken);
                    return replayCheck.IsError ? replayCheck.Errors : acquired.Response;
                case IdempotencyAcquireOutcome.InProgress:
                    return Errors.SapTransferApproval.AddInProgress;
                case IdempotencyAcquireOutcome.RequestMismatch:
                    return Errors.Idempotency.RequestMismatch("held transfer add");
                case IdempotencyAcquireOutcome.Acquired:
                    idempotencyRequestId = acquired.RequestId;
                    release = true;
                    break;
            }

            var draft = await sap.GetStockTransferDraftAsync(command.DraftEntry, cancellationToken);
            if (draft is null || !SapTransferApprovalProjection.IsTransferDraft(draft))
            {
                return Errors.SapTransferApproval.NotFound(command.DraftEntry);
            }

            // Before any state is described: a transfer out of someone else's warehouse is not theirs
            // to learn anything about.
            var allowed = await EnsureMayAddAsync(
                command.UserId, SapTransferApprovalProjection.SourceWarehouses(draft), cancellationToken);
            if (allowed.IsError)
            {
                return allowed.Errors;
            }

            if (!SapTransferApprovalProjection.IsOpen(draft))
            {
                return await AlreadyAddedAsync(draft, cancellationToken);
            }

            if (!SapTransferApprovalProjection.IsApproved(draft))
            {
                var state = SapStockTransferAuthorizationStatuses.ToDisplay(draft.AuthorizationStatus);
                return Errors.SapTransferApproval.NotApproved(string.IsNullOrWhiteSpace(state) ? "not approved" : state);
            }

            var requests = await sap.GetStockTransferApprovalRequestsAsync([draft.DocEntry], withLines: false, cancellationToken);
            if (SapTransferApprovalProjection.Unsettled(requests) is { } unsettled)
            {
                return Errors.SapTransferApproval.ApprovalNotSettled(
                    unsettled.Code, SapApprovalRequestStatuses.ToDisplay(unsettled.Status));
            }

            // SAP refuses the whole transfer for one batch-managed line without a complete pick, so
            // say which lines before asking. Without the flags SAP's own refusal is the answer.
            var management = await TryReadItemManagementAsync(draft, cancellationToken);
            var (_, missingPicks) = SapTransferApprovalProjection.Lines(draft, management);
            if (missingPicks.Count > 0)
            {
                return Errors.SapTransferApproval.SelectionMissing(missingPicks);
            }

            var linked = new List<(int DocEntry, InventoryTransferRequest? Request)>();
            foreach (var requestDocEntry in SapTransferApprovalProjection.LinkedRequestEntries(draft))
            {
                var held = await HoldLinkedRequestAsync(requestDocEntry, requestLocks, cancellationToken);
                if (held.IsError)
                {
                    return held.Errors;
                }

                linked.Add((requestDocEntry, held.Value));
            }

            if (await RefuseIfTillSalesWouldBeTakenAsync(draft, cancellationToken) is { } refusal)
            {
                return refusal;
            }

            // Which transfer was newest before the add, so the one it produces can be told apart
            // afterwards if SAP's answer does not name it.
            var newestBefore = await TryReadNewestTransferAsync();

            // The last safe abort: nothing has reached SAP yet.
            cancellationToken.ThrowIfCancellationRequested();

            int? createdDocEntry;
            try
            {
                createdDocEntry = await sap.SaveStockTransferDraftToDocumentAsync(draft.DocEntry, CancellationToken.None);
            }
            catch (SapRequestRejectedException rejected)
            {
                logger.LogWarning(rejected, "SAP refused to add stock transfer draft {DraftEntry}", draft.DocEntry);
                await TryAuditAsync(command, draft, false, $"SAP refused: {rejected.SapMessage}");
                return SapTransferApprovalProjection.IsSelectionRefusal(rejected.SapMessage)
                    ? Errors.SapTransferApproval.SelectionRefusedBySap(rejected.SapMessage)
                    : Errors.SapTransferApproval.SapRejected(rejected.SapMessage);
            }
            catch (Exception exception)
            {
                // The add may or may not have happened. The draft is the witness: SAP closes it the
                // moment it converts, and it survives either way.
                var draftAfter = await TryReadDraftAsync(draft.DocEntry);
                if (draftAfter is null || SapTransferApprovalProjection.IsOpen(draftAfter))
                {
                    logger.LogError(exception, "Adding stock transfer draft {DraftEntry} got no clear answer from SAP", draft.DocEntry);
                    await TryAuditAsync(command, draft, false, $"No clear answer from SAP: {exception.Message}");
                    return Errors.SapTransferApproval.AddUncertain;
                }

                logger.LogWarning(exception, "Stock transfer draft {DraftEntry} was added although the call failed; it is closed in SAP", draft.DocEntry);
                createdDocEntry = null;
            }

            var docEntry = createdDocEntry ?? await TryIdentifyCreatedTransferAsync(draft, newestBefore);
            var transfer = docEntry is int entry ? await TryReadTransferAsync(entry) : null;

            var closed = new List<int>();
            var leftOpen = new List<string>();
            foreach (var (requestDocEntry, before) in linked)
            {
                await CloseLinkedRequestAsync(requestDocEntry, before, closed, leftOpen);
                if (transfer is not null)
                {
                    await TryMarkGeneratedAsync(requestDocEntry, transfer, command.UserId);
                }
            }

            var result = new AddApprovedSapTransferResultDto
            {
                DraftEntry = draft.DocEntry,
                DraftNum = draft.DocNum,
                FromWarehouse = draft.FromWarehouse,
                ToWarehouse = draft.ToWarehouse,
                TransferDocEntry = transfer?.DocEntry ?? docEntry,
                TransferDocNum = transfer?.DocNum,
                Resolved = docEntry is not null,
                ClosedRequestDocNums = closed,
                RequestsLeftOpen = leftOpen,
                Message = Describe(draft, transfer, docEntry, closed, leftOpen)
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
            return Errors.SapTransferApproval.Cancelled;
        }
        catch (SapRequestRejectedException rejected)
        {
            return Errors.SapTransferApproval.SapRejected(rejected.SapMessage);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not add stock transfer draft {DraftEntry}", command.DraftEntry);
            return Errors.SapTransferApproval.SapUnavailable(exception.Message);
        }
        finally
        {
            foreach (var lockId in requestLocks)
            {
                await TryReleaseAsync(lockId, "transfer request conversion lock", command.DraftEntry);
            }

            if (release && idempotencyRequestId.HasValue)
            {
                await TryReleaseAsync(idempotencyRequestId.Value, "held transfer add lock", command.DraftEntry);
            }
        }
    }

    /// <summary>
    /// The conversion rule, for every warehouse the draft takes stock out of. A draft that names none
    /// is asked about with none, which a warehouse-scoped user is refused.
    /// </summary>
    private async Task<ErrorOr<Success>> EnsureMayAddAsync(
        Guid userId,
        IReadOnlyCollection<string?> sourceWarehouses,
        CancellationToken cancellationToken)
    {
        var sources = sourceWarehouses.Where(code => !string.IsNullOrWhiteSpace(code)).ToList();
        if (sources.Count == 0)
        {
            return await warehouseAuthorizer.EnsureCanConvertRequestAsync(userId, null, cancellationToken);
        }

        foreach (var source in sources)
        {
            var check = await warehouseAuthorizer.EnsureCanConvertRequestAsync(userId, source, cancellationToken);
            if (check.IsError)
            {
                return check.Errors;
            }
        }

        return Result.Success;
    }

    /// <summary>
    /// A closed draft has been added — by an earlier add here or in the B1 client. Names the transfer
    /// when SAP's request says which it was, and any request it was copied from that is still open.
    /// </summary>
    private async Task<Error> AlreadyAddedAsync(SAPStockTransferDraft draft, CancellationToken cancellationToken)
    {
        int? transferDocNum = null;
        try
        {
            var requests = await sap.GetStockTransferApprovalRequestsAsync([draft.DocEntry], withLines: false, cancellationToken);
            if (requests.Select(request => request.ObjectEntry).FirstOrDefault(entry => entry is > 0) is int objectEntry)
            {
                transferDocNum = (await sap.GetInventoryTransferByDocEntryAsync(objectEntry, cancellationToken))?.DocNum;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not read which transfer draft {DraftEntry} became", draft.DocEntry);
        }

        var stillOpen = new List<string>();
        foreach (var requestDocEntry in SapTransferApprovalProjection.LinkedRequestEntries(draft))
        {
            try
            {
                var request = await sap.GetInventoryTransferRequestByDocEntryAsync(requestDocEntry, cancellationToken);
                if (request is not null && !SapTransferApprovalProjection.IsClosed(request))
                {
                    stillOpen.Add(
                        $"Transfer request #{request.DocNum}, which it was copied from, is still open in SAP; close it on the " +
                        "Transfers page so it is not converted a second time.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not read transfer request {DocEntry} behind added draft {DraftEntry}", requestDocEntry, draft.DocEntry);
            }
        }

        return Errors.SapTransferApproval.AlreadyAdded(draft.DocNum, transferDocNum, stillOpen);
    }

    /// <summary>
    /// Takes the conversion lock on a transfer request the draft was copied from, and refuses when this
    /// app has already converted it — that transfer moved the stock, and adding the draft would move it
    /// again.
    /// </summary>
    /// <remarks>
    /// The same lock and the same marker as <see cref="ConvertTransferRequestHandler"/>: a conversion of
    /// the same request started while this add runs collides with it instead of racing it.
    /// </remarks>
    private async Task<ErrorOr<InventoryTransferRequest?>> HoldLinkedRequestAsync(
        int requestDocEntry,
        List<long> locks,
        CancellationToken cancellationToken)
    {
        var acquired = await idempotencyRequestStore.TryAcquireAsync<int>(
            ConvertTransferRequestHandler.ConversionPostScope,
            requestDocEntry.ToString(),
            new { DocEntry = requestDocEntry },
            cancellationToken);
        switch (acquired.Outcome)
        {
            case IdempotencyAcquireOutcome.InProgress:
                return Errors.SapTransferApproval.ConversionInProgress(requestDocEntry);
            case IdempotencyAcquireOutcome.RequestMismatch:
                return Errors.Idempotency.RequestMismatch("transfer request conversion");
        }

        if (acquired.RequestId is long lockId)
        {
            locks.Add(lockId);
        }

        try
        {
            var request = await sap.GetInventoryTransferRequestByDocEntryAsync(requestDocEntry, cancellationToken);
            if (request is null)
            {
                // Gone from SAP: nothing can convert it again, so there is nothing to guard.
                return (InventoryTransferRequest?)null;
            }

            var earlier = await sap.FindStockTransferByOrderNumberAsync(
                InventoryTransferRequestConversion.OrderNumberFor(requestDocEntry),
                InventoryTransferRequestConversion.LookupFrom(request, DateTime.Today),
                cancellationToken);
            if (earlier is not null)
            {
                logger.LogWarning(
                    "Stock transfer draft copied from request {RequestDocEntry} refused: the app already converted it into transfer {TransferDocEntry}",
                    requestDocEntry, earlier.DocEntry);
                return Errors.SapTransferApproval.AlreadyTransferredByApp(request.DocNum, earlier.DocNum);
            }

            return request;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Could not check transfer request {DocEntry} for an earlier conversion", requestDocEntry);
            return Errors.SapTransferApproval.SapUnavailable(
                $"could not check transfer request {requestDocEntry}, which this draft was copied from, for an earlier conversion. Try again in a minute");
        }
    }

    /// <summary>
    /// Refuses the add when the transfer would take units a till has sold out of the source warehouse
    /// and SAP has not yet invoiced — the check a transfer request conversion makes, for the same reason.
    /// </summary>
    /// <remarks>
    /// The till claims are a local read and are asked first, so a draft out of a warehouse no till sells
    /// from costs nothing more. A stock read that fails refuses rather than adds.
    /// </remarks>
    private async Task<Error?> RefuseIfTillSalesWouldBeTakenAsync(
        SAPStockTransferDraft draft,
        CancellationToken cancellationToken)
    {
        // In the inventory unit, which is what the till claims and SAP's stock count in.
        var transfer = new CreateInventoryTransferRequest
        {
            FromWarehouse = draft.FromWarehouse,
            ToWarehouse = draft.ToWarehouse,
            Lines = (draft.StockTransferLines ?? [])
                .Select(line => new CreateInventoryTransferLineRequest
                {
                    ItemCode = line.ItemCode,
                    Quantity = line.InventoryQuantity is > 0 ? line.InventoryQuantity.Value : line.Quantity,
                    FromWarehouseCode = line.FromWarehouseCode ?? draft.FromWarehouse,
                    ToWarehouseCode = line.WarehouseCode ?? draft.ToWarehouse
                })
                .ToList()
        };

        var anyHeld = false;
        foreach (var source in transfer.Lines
                     .Where(line => !string.IsNullOrWhiteSpace(line.FromWarehouseCode))
                     .GroupBy(line => line.FromWarehouseCode!, StringComparer.OrdinalIgnoreCase))
        {
            var claims = await tillClaims.ForWarehouseAsync(source.Key, cancellationToken);
            if (source.Any(line => line.ItemCode is { } itemCode && claims.ContainsKey(itemCode)))
            {
                anyHeld = true;
                break;
            }
        }

        if (!anyHeld)
        {
            return null;
        }

        var validation = await stockValidation.ValidateInventoryTransferStockAsync(transfer, cancellationToken);
        if (!validation.StockWasFullyRead)
        {
            return Errors.SapTransferApproval.StockUnreadable(string.Join(", ", validation.UnreadableWarehouses));
        }

        if (validation.IsValid)
        {
            return null;
        }

        logger.LogWarning(
            "Refused to add stock transfer draft {DraftEntry} from {Warehouse}: {Shortages}",
            draft.DocEntry, draft.FromWarehouse, string.Join("; ", validation.Errors.Select(error => error.Message)));

        return Errors.SapTransferApproval.TillSalesWouldBeTaken(
            string.Join("; ", validation.Errors.Select(error => error.Message)));
    }

    /// <summary>
    /// The transfer the add produced, when SAP's answer did not name it: the approval request's
    /// <c>ObjectEntry</c>, which the B1 client fills when it adds a draft, else the one transfer posted
    /// since the add began between the same warehouses carrying exactly the draft's lines.
    /// </summary>
    /// <remarks>
    /// Never a match on DocNum — drafts and transfers number from different series. Two identical
    /// transfers posted in that window are left unresolved rather than guessed between.
    /// </remarks>
    private async Task<int?> TryIdentifyCreatedTransferAsync(SAPStockTransferDraft draft, int? newestBefore)
    {
        try
        {
            var requests = await sap.GetStockTransferApprovalRequestsAsync([draft.DocEntry], withLines: false, CancellationToken.None);
            if (requests.Select(request => request.ObjectEntry).FirstOrDefault(entry => entry is > 0) is int objectEntry)
            {
                return objectEntry;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read the approval requests of draft {DraftEntry} after the add", draft.DocEntry);
        }

        if (newestBefore is not int before)
        {
            return null;
        }

        try
        {
            var candidates = await sap.GetStockTransfersAfterAsync(before, draft.FromWarehouse, draft.ToWarehouse, CancellationToken.None);
            var matches = candidates.Where(candidate => SapTransferApprovalProjection.CarriesSameLines(draft, candidate)).ToList();
            if (matches.Count == 1)
            {
                return matches[0].DocEntry;
            }

            if (matches.Count > 1)
            {
                logger.LogWarning(
                    "{Count} transfers posted since the add of draft {DraftEntry} carry its lines; none is claimed as the one it became",
                    matches.Count, draft.DocEntry);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not look for the transfer draft {DraftEntry} became", draft.DocEntry);
        }

        return null;
    }

    /// <summary>
    /// Closes a transfer request the draft was copied from once the transfer is in. One SAP closed itself
    /// through the line link counts as closed; one that could not be closed is said, never thrown —
    /// the stock has moved.
    /// </summary>
    private async Task CloseLinkedRequestAsync(
        int requestDocEntry,
        InventoryTransferRequest? before,
        List<int> closed,
        List<string> leftOpen)
    {
        if (before is null)
        {
            return;
        }

        InventoryTransferRequest? current;
        try
        {
            current = await sap.GetInventoryTransferRequestByDocEntryAsync(requestDocEntry, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read transfer request {DocEntry} back after the add", requestDocEntry);
            current = before;
        }

        if (current is null || SapTransferApprovalProjection.IsClosed(current))
        {
            closed.Add(before.DocNum);
            return;
        }

        try
        {
            await sap.CloseInventoryTransferRequestAsync(requestDocEntry, CancellationToken.None);
            closed.Add(before.DocNum);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Transfer request {DocEntry} could not be closed after its draft was added", requestDocEntry);
            leftOpen.Add(
                $"Transfer request #{before.DocNum}, which the draft was copied from, could not be closed in SAP; " +
                "close it on the Transfers page so it is not converted a second time.");
        }
    }

    /// <summary>Marks the app's own approval of a linked request generated, when it has one.</summary>
    private async Task TryMarkGeneratedAsync(int requestDocEntry, InventoryTransfer transfer, Guid userId)
    {
        try
        {
            await approvalService.MarkGeneratedAsync(
                requestDocEntry, transfer.DocEntry, transfer.DocNum, userId, byAuthorizer: false, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not mark the app's approval of transfer request {DocEntry} generated", requestDocEntry);
        }
    }

    private static string Describe(
        SAPStockTransferDraft draft,
        InventoryTransfer? transfer,
        int? docEntry,
        IReadOnlyCollection<int> closed,
        IReadOnlyCollection<string> leftOpen)
    {
        var added = transfer is not null
            ? $"Inventory transfer #{transfer.DocNum} added to SAP from draft {draft.DocNum}."
            : docEntry is int entry
                ? $"Inventory transfer DocEntry {entry} added to SAP from draft {draft.DocNum}."
                : $"Draft {draft.DocNum} was added to SAP, but SAP did not say which transfer it became; " +
                  $"it is the newest transfer from {draft.FromWarehouse} to {draft.ToWarehouse} on the Transfers page.";

        var closedText = closed.Count == 0
            ? string.Empty
            : closed.Count == 1
                ? $" Transfer request #{closed.First()} is closed."
                : $" Transfer requests #{string.Join(", #", closed)} are closed.";

        var openText = leftOpen.Count == 0 ? string.Empty : " " + string.Join(" ", leftOpen);

        return added + closedText + openText;
    }

    private async Task<IReadOnlyDictionary<string, SapItemManagement>?> TryReadItemManagementAsync(
        SAPStockTransferDraft draft,
        CancellationToken cancellationToken)
    {
        try
        {
            return await sap.GetItemManagementAsync(
                draft.StockTransferLines?.Select(line => line.ItemCode ?? string.Empty).ToList() ?? [],
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not read the batch and serial flags for draft {DraftEntry}; SAP will decide at the add", draft.DocEntry);
            return null;
        }
    }

    private async Task<int?> TryReadNewestTransferAsync()
    {
        try
        {
            return await sap.GetNewestStockTransferDocEntryAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read the newest stock transfer before an add");
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
            logger.LogWarning(exception, "Could not read stock transfer draft {DraftEntry} back after the add", draftEntry);
            return null;
        }
    }

    private async Task<InventoryTransfer?> TryReadTransferAsync(int docEntry)
    {
        try
        {
            return await sap.GetInventoryTransferByDocEntryAsync(docEntry, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read stock transfer {DocEntry} back after the add", docEntry);
            return null;
        }
    }

    private async Task TryAuditAsync(AddApprovedSapTransferCommand command, SAPStockTransferDraft draft, bool success, string outcome)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.AddApprovedSapTransfer,
                "SapStockTransferDraft",
                draft.DocEntry.ToString(),
                $"Add approved stock transfer draft {draft.DocNum} (DocEntry {draft.DocEntry}, {draft.FromWarehouse} to {draft.ToWarehouse}) by {command.Username}. {outcome}",
                success,
                success ? null : outcome);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to audit the add of stock transfer draft {DraftEntry}", draft.DocEntry);
        }
    }

    private async Task TryReleaseAsync(long requestId, string what, int draftEntry)
    {
        try
        {
            await idempotencyRequestStore.ReleaseAsync(requestId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to release the {What} for stock transfer draft {DraftEntry}", what, draftEntry);
        }
    }
}
