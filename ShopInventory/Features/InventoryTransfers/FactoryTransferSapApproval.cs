using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers.SapApproval;
using ShopInventory.Features.SapTransferApprovals;
using ShopInventory.Features.SapTransferApprovals.Commands.AddApprovedSapTransfer;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers;

/// <summary>What one step of a factory transfer came to.</summary>
/// <param name="Transfer">The transfer in SAP, once it is there.</param>
/// <param name="Message">What to tell the person, in full.</param>
public sealed record FactoryTransferStep(InventoryTransferDto? Transfer, string Message);

/// <summary>
/// Moves a factory transfer (<see cref="TransferTypes"/>) one step through SAP's own approval: saves it to
/// SAP as a draft, reports where the draft stands, adds it once SAP has approved it — or, on a route no
/// SAP template covers, posts it.
/// </summary>
public interface IFactoryTransferSapApproval
{
    /// <summary>
    /// The next step for <paramref name="pending"/>, a factory transfer the app has cleared. Records the
    /// outcome on the (tracked) entity and saves it. The caller has checked who may act.
    /// </summary>
    Task<ErrorOr<FactoryTransferStep>> AdvanceAsync(PendingInventoryTransferEntity pending, Guid userId, string username);
}

/// <remarks>
/// <para>SAP's templates never run for a document the Service Layer sends (tested 2026-11-06 with
/// "Enable Approval Process in DI" off and on), so this does what <see cref="Commands.ConvertTransferRequest.ConvertTransferRequestHandler"/>
/// does for a request: the transfer is saved as a SAP draft, a B1 user adds the draft in the B1 client —
/// which is what makes SAP's template hold it for SAP's approver — and once SAP has approved it, this
/// adds it. The draft carries the transfer's draft number in <c>U_OrderNumber</c>, the marker its posted
/// transfer would carry, so the draft is found again, and so is the transfer it becomes, wherever it is
/// added: here, on SAP-Held Transfers, or in the B1 client.</para>
///
/// <para>Each step reads SAP first, in this order: a transfer carrying the marker (added already: record
/// it), an open draft carrying it (say where it stands, add it when approved, stop when rejected), and
/// only then the route. A draft closed without a transfer was removed in the B1 client; the step saves a
/// new one, as a conversion does.</para>
///
/// <para>One step per transfer at a time (<see cref="LockScope"/>), so two presses cannot save two
/// drafts. Past the caller's checks every SAP call runs on <see cref="CancellationToken.None"/>: a draft
/// saved and not recorded would be saved again.</para>
/// </remarks>
public sealed class FactoryTransferSapApproval(
    ApplicationDbContext context,
    ISAPServiceLayerClient sap,
    ISapTransferApprovalRoutes sapRoutes,
    ISapDraftApprovalNotice draftNotice,
    IPendingInventoryTransferPoster poster,
    IIdempotencyRequestStore idempotencyRequestStore,
    IAuditService auditService,
    ISender sender,
    ILogger<FactoryTransferSapApproval> logger) : IFactoryTransferSapApproval
{
    internal const string LockScope = "factory-transfer-sap-step";

    /// <summary>A transfer is dated by its own date or the day it posted; the lookup reaches back a little.</summary>
    private static readonly TimeSpan LookupSlack = TimeSpan.FromDays(2);

    public async Task<ErrorOr<FactoryTransferStep>> AdvanceAsync(
        PendingInventoryTransferEntity pending,
        Guid userId,
        string username)
    {
        var cancellationToken = CancellationToken.None;
        var marker = PendingInventoryTransferPoster.SapOrderNumber(pending);
        if (marker is null)
            return Errors.InventoryTransfer.InvalidOperation(
                "This transfer has no draft number, so its SAP draft could not be found again. Post it from Inventory transfers.");

        var acquired = await idempotencyRequestStore.TryAcquireAsync<int>(
            LockScope, pending.Id.ToString(), new { PendingTransferId = pending.Id }, cancellationToken);
        switch (acquired.Outcome)
        {
            case IdempotencyAcquireOutcome.InProgress:
                return Errors.InventoryTransfer.PostInProgress;
            case IdempotencyAcquireOutcome.RequestMismatch:
                return Errors.Idempotency.RequestMismatch("factory transfer");
        }

        // A mutex only, never completed: SAP, not a stored reply, says where the transfer is.
        try
        {
            return await StepAsync(pending, marker, userId, username, cancellationToken);
        }
        finally
        {
            if (acquired.RequestId is { } lockId)
            {
                try { await idempotencyRequestStore.ReleaseAsync(lockId, CancellationToken.None); }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Failed to release the SAP step lock for factory transfer {PendingId}", pending.Id);
                }
            }
        }
    }

    private async Task<ErrorOr<FactoryTransferStep>> StepAsync(
        PendingInventoryTransferEntity pending,
        string marker,
        Guid userId,
        string username,
        CancellationToken cancellationToken)
    {
        var route = $"{pending.FromWarehouse} to {pending.ToWarehouse}";

        // 1. Already a transfer: added in the B1 client, on SAP-Held Transfers, or by an earlier step
        //    whose answer was lost.
        InventoryTransfer? inSap;
        List<SAPStockTransferDraft> drafts;
        try
        {
            var datedFrom = pending.DocDate is { } docDate && docDate < pending.CreatedAtUtc ? docDate : pending.CreatedAtUtc;
            inSap = await sap.FindStockTransferByOrderNumberAsync(marker, datedFrom.Date - LookupSlack, cancellationToken);
            drafts = inSap is null
                ? await sap.FindStockTransferDraftsByOrderNumberAsync(marker, cancellationToken)
                : [];
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not read SAP for factory transfer {PendingId} ({Marker})", pending.Id, marker);
            return Errors.InventoryTransfer.SapConnectionError(
                $"Could not read SAP for transfer {marker}, so nothing was sent or added. Try again in a minute.");
        }

        if (inSap is not null)
        {
            var recorded = await poster.RecordAddedInSapAsync(pending, inSap, userId,
                $"Factory transfer {marker} ({route}) found in SAP as transfer #{inSap.DocNum}, added from its SAP-approved draft");
            return new FactoryTransferStep(recorded,
                $"Transfer {marker} is in SAP as inventory transfer #{inSap.DocNum}: SAP approved it and it has been added.");
        }

        // 2. The draft an earlier step saved.
        if (drafts.FirstOrDefault(SapTransferApprovalProjection.IsOpen) is { } open)
            return await FollowDraftAsync(pending, open, marker, route, userId, username);

        if (drafts.Count > 0)
        {
            logger.LogWarning(
                "Factory transfer {PendingId}: SAP draft(s) {Drafts} marked {Marker} are closed and no transfer carries the marker; " +
                "they were removed in the B1 client", pending.Id, string.Join(", ", drafts.Select(draft => draft.DocEntry)), marker);
        }

        // 3. Nothing in SAP yet: SAP's templates decide whether it waits for SAP's approval.
        SapTransferRoute verdict;
        CreateInventoryTransferRequest payload;
        try
        {
            payload = PendingInventoryTransferMapper.DeserializePayload(pending);
        }
        catch (InvalidOperationException exception)
        {
            return Errors.InventoryTransfer.InvalidOperation(exception.Message);
        }

        try
        {
            verdict = await sapRoutes.ForRouteAsync(
                pending.FromWarehouse,
                pending.ToWarehouse,
                (payload.Lines ?? []).SelectMany(line => new[] { line.FromWarehouseCode, line.ToWarehouseCode }),
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not read SAP's transfer approval templates for factory transfer {PendingId}", pending.Id);
            return Errors.InventoryTransfer.SapConnectionError(
                $"Could not read SAP's approval templates, so transfer {marker} could not be checked against SAP's own " +
                "approval and nothing was sent. Try again in a minute.");
        }

        if (!verdict.IsCovered)
        {
            // No SAP template holds this route: B1 would post it as keyed, and so does this.
            logger.LogInformation(
                "Factory transfer {PendingId} ({Route}) is on no SAP approval template's route; posting it", pending.Id, route);
            var posted = await poster.PostAsync(pending, userId);
            return posted.IsError
                ? posted.Errors
                : new FactoryTransferStep(posted.Value,
                    $"No SAP approval template covers {route}, so inventory transfer #{posted.Value.DocNum} was posted to SAP.");
        }

        return await SaveDraftAsync(pending, payload, marker, route, verdict);
    }

    /// <summary>Saves the transfer to SAP as a draft for SAP's approval.</summary>
    /// <remarks>
    /// The messages name the draft number and the order number together. SAP's draft numbers repeat: on
    /// KEFALOS_TEST_3 (2026-11-16) this step's draft 80191 and PR #907's draft 80189 both read DocNum 112390,
    /// so the order number is what tells a B1 user which draft to add.
    /// </remarks>
    private async Task<ErrorOr<FactoryTransferStep>> SaveDraftAsync(
        PendingInventoryTransferEntity pending,
        CreateInventoryTransferRequest payload,
        string marker,
        string route,
        SapTransferRoute verdict)
    {
        payload.OrderNumber = marker;
        // No approval stages to name: SAP's approval is the one that applies, and SAP records it.
        payload.Comments = InventoryTransferRemarks.Build(pending, [], payload.Comments);
        pending.LastAttemptedAtUtc = DateTime.UtcNow;

        SAPStockTransferDraft draft;
        try
        {
            draft = await sap.CreateStockTransferDraftAsync(payload, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "SAP did not save factory transfer {PendingId} ({Marker}) as a draft", pending.Id, marker);
            var reason = $"SAP did not save the transfer as a draft for its approval: {exception.Message}";
            pending.Status = PendingInventoryTransferStatuses.PostFailed;
            pending.LastError = reason.Length > 2000 ? reason[..2000] : reason;
            await context.SaveChangesAsync(CancellationToken.None);
            return Errors.InventoryTransfer.CreationFailed(reason);
        }

        var templates = verdict.TemplateNames;
        pending.Status = PendingInventoryTransferStatuses.HeldInSap;
        pending.SapDraftDocEntry = draft.DocEntry;
        pending.SapDraftDocNum = draft.DocNum;
        pending.SapApprovalState = FactoryTransferSapStates.AwaitingSapClient;
        pending.SapApprovalTemplates = Truncate(string.Join(", ", verdict.Templates.Select(template => template.Name)), 500);
        pending.SapCheckedAtUtc = DateTime.UtcNow;
        pending.LastError = null;
        await context.SaveChangesAsync(CancellationToken.None);

        logger.LogInformation(
            "Factory transfer {PendingId} ({Route}) saved to SAP as draft {DraftEntry} for SAP's approval ({Templates})",
            pending.Id, route, draft.DocEntry, templates);
        await TryAuditAsync(pending,
            $"Factory transfer {marker} ({route}) saved to SAP as draft {draft.DocNum} (DocEntry {draft.DocEntry}) for SAP's approval: {templates}");

        // Only here, when the draft is first saved: a later press finds the draft and says where it stands,
        // and must not message the approvers again.
        var notice = await draftNotice.SendAsync(
            draft, verdict, $"Factory transfer {marker} ({route})", marker, CancellationToken.None);

        return new FactoryTransferStep(null,
            $"SAP's approval {templates} covers {route}, so transfer {marker} was saved to SAP as draft {draft.DocNum}. " +
            $"Nothing has moved yet. Add draft {draft.DocNum} (order number {marker}) in the SAP client to send it to " +
            $"SAP's approver; once SAP approves it, press Check SAP here and it posts. {notice.Sentence}");
    }

    /// <summary>Says where an open draft stands with SAP's approval, and adds it once SAP has approved it.</summary>
    private async Task<ErrorOr<FactoryTransferStep>> FollowDraftAsync(
        PendingInventoryTransferEntity pending,
        SAPStockTransferDraft draft,
        string marker,
        string route,
        Guid userId,
        string username)
    {
        var status = draft.AuthorizationStatus;
        var templates = string.IsNullOrWhiteSpace(pending.SapApprovalTemplates)
            ? "SAP's approval"
            : $"SAP's approval ({pending.SapApprovalTemplates})";
        pending.SapDraftDocEntry = draft.DocEntry;
        pending.SapDraftDocNum = draft.DocNum;
        pending.SapCheckedAtUtc = DateTime.UtcNow;

        if (Is(status, SapStockTransferAuthorizationStatuses.Rejected))
        {
            pending.Status = PendingInventoryTransferStatuses.Rejected;
            pending.SapApprovalState = FactoryTransferSapStates.Rejected;
            pending.DecidedAtUtc = DateTime.UtcNow;
            pending.LastError = $"Rejected in SAP: {templates} did not approve draft {draft.DocNum}.";
            await context.SaveChangesAsync(CancellationToken.None);
            await TryAuditAsync(pending, $"Factory transfer {marker} ({route}) rejected in SAP (draft {draft.DocNum})");
            return new FactoryTransferStep(null,
                $"{templates} rejected draft {draft.DocNum}, so transfer {marker} will not post. Nothing has moved.");
        }

        if (!Is(status, SapStockTransferAuthorizationStatuses.Approved))
        {
            var pendingInSap = Is(status, SapStockTransferAuthorizationStatuses.Pending);
            pending.Status = PendingInventoryTransferStatuses.HeldInSap;
            pending.SapApprovalState = pendingInSap ? FactoryTransferSapStates.Pending : FactoryTransferSapStates.AwaitingSapClient;
            await context.SaveChangesAsync(CancellationToken.None);
            return new FactoryTransferStep(null, pendingInSap
                ? $"Transfer {marker} is waiting for {templates} as SAP draft {draft.DocNum}. Nothing has moved yet; " +
                  "once SAP approves it, press Check SAP and it posts."
                : $"Transfer {marker} is in SAP as draft {draft.DocNum} (order number {marker}), waiting to be added in " +
                  $"the SAP client — adding it there sends it to {templates}. Nothing has moved yet.");
        }

        // SAP approved it: add it, through the SAP-held transfers add, which checks the batch picks and the
        // till's unposted sales and recognises the transfer it becomes.
        pending.Status = PendingInventoryTransferStatuses.HeldInSap;
        pending.SapApprovalState = FactoryTransferSapStates.Approved;
        await context.SaveChangesAsync(CancellationToken.None);

        var added = await sender.Send(
            new AddApprovedSapTransferCommand(draft.DocEntry, userId, username, ClientRequestId: null, CallerAuthorized: true),
            CancellationToken.None);
        if (added.IsError)
        {
            pending.LastError = Truncate(added.FirstError.Description, 2000);
            await context.SaveChangesAsync(CancellationToken.None);
            return added.Errors;
        }

        if (added.Value.TransferDocEntry is not int transferDocEntry)
        {
            // Added, but SAP's answer named no transfer and none could be told apart: the next step finds
            // it by the marker.
            return new FactoryTransferStep(null,
                $"SAP approved draft {draft.DocNum} and it was added. {added.Value.Message} Press Check SAP to link the transfer here.");
        }

        InventoryTransfer transfer;
        try
        {
            transfer = await sap.GetInventoryTransferByDocEntryAsync(transferDocEntry, CancellationToken.None)
                ?? Minimal(added.Value, transferDocEntry);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read back transfer {DocEntry} added from SAP draft {DraftEntry}", transferDocEntry, draft.DocEntry);
            transfer = Minimal(added.Value, transferDocEntry);
        }

        var recorded = await poster.RecordAddedInSapAsync(pending, transfer, userId,
            $"Factory transfer {marker} ({route}): SAP-approved draft {draft.DocNum} added as transfer #{transfer.DocNum}");
        return new FactoryTransferStep(recorded,
            $"SAP approved draft {draft.DocNum}, and it was added as inventory transfer #{transfer.DocNum}.");
    }

    private static InventoryTransfer Minimal(AddApprovedSapTransferResultDto added, int docEntry) => new()
    {
        DocEntry = docEntry,
        DocNum = added.TransferDocNum ?? 0,
        FromWarehouse = added.FromWarehouse,
        ToWarehouse = added.ToWarehouse
    };

    private async Task TryAuditAsync(PendingInventoryTransferEntity pending, string detail)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.SubmitTransferForApproval, "PendingInventoryTransfer", pending.Id.ToString(), detail, true);
        }
        catch { }
    }

    private static bool Is(string? status, string expected)
        => string.Equals(status, expected, StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
