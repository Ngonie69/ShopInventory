using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Common.Validation;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.Commands.EditPendingTransferLines;

public sealed class EditPendingTransferLinesHandler(
    ApplicationDbContext context,
    ITransferWarehouseAuthorizer warehouseAuthorizer,
    IIdempotencyRequestStore idempotencyRequestStore,
    IAuditService auditService,
    ILogger<EditPendingTransferLinesHandler> logger)
    : IRequestHandler<EditPendingTransferLinesCommand, ErrorOr<PendingInventoryTransferDecisionResponseDto>>
{
    public async Task<ErrorOr<PendingInventoryTransferDecisionResponseDto>> Handle(
        EditPendingTransferLinesCommand command,
        CancellationToken cancellationToken)
    {
        var pending = await context.PendingInventoryTransfers
            .AsTracking()
            .FirstOrDefaultAsync(item => item.Id == command.PendingTransferId, cancellationToken);
        if (pending is null)
            return Errors.InventoryTransfer.PendingTransferNotFound(command.PendingTransferId);

        if (pending.Status is not (PendingInventoryTransferStatuses.Approved or PendingInventoryTransferStatuses.PostFailed))
            return Errors.InventoryTransfer.LineEditNotAllowed(pending.Status);

        // A post whose answer was lost may already be in SAP. Changing the lines and posting again
        // would put a second, different document beside it, so SAP has to be checked first.
        if (pending.Status == PendingInventoryTransferStatuses.PostFailed
            && PendingTransferFailureClassifier.Classify(pending.LastError).Kind == PendingTransferFailureKinds.OutcomeUnknown)
        {
            return Errors.InventoryTransfer.PostOutcomeUnknown;
        }

        var scopeCheck = await warehouseAuthorizer.EnsureCanActOnSourceAsync(
            command.UserId, pending.FromWarehouse, cancellationToken);
        if (scopeCheck.IsError)
            return scopeCheck.Errors;

        CreateInventoryTransferRequest payload;
        try
        {
            payload = PendingInventoryTransferMapper.DeserializePayload(pending);
        }
        catch (InvalidOperationException exception)
        {
            return Errors.InventoryTransfer.ValidationFailed(exception.Message);
        }

        var edited = ApplyEdits(payload, command.Lines);
        if (edited.IsError)
            return edited.Errors;

        var (lines, changes) = edited.Value;

        // A post in flight leaves the record reading Approved or PostFailed until SAP answers, and
        // reads this payload when it starts. Taking the poster's own claim stops the lines changing
        // under a post, and a post completing after the record was read being edited afterwards.
        var acquired = await idempotencyRequestStore.TryAcquireAsync<InventoryTransferDto>(
            PendingInventoryTransferPoster.IdempotencyScope,
            pending.Id.ToString(),
            new { PendingTransferId = pending.Id },
            cancellationToken);
        switch (acquired.Outcome)
        {
            case IdempotencyAcquireOutcome.InProgress:
                return Errors.InventoryTransfer.PostInProgress;
            case IdempotencyAcquireOutcome.ReplayAvailable:
                return Errors.InventoryTransfer.LineEditNotAllowed(PendingInventoryTransferStatuses.Posted);
            case IdempotencyAcquireOutcome.RequestMismatch:
                return Errors.Idempotency.RequestMismatch("inventory transfer line edit");
        }

        try
        {
            pending.PayloadJson = PendingInventoryTransferMapper.SerializePayload(
                PendingInventoryTransferMapper.WithLines(payload, lines));
            pending.LineCount = lines.Count;
            pending.TotalQuantity = lines.Sum(line => line.Quantity);
            // The failure was against the lines as they were. Left in place it would go on saying the
            // transfer is short after the short line was fixed, so it reads as approved and unposted —
            // which is what it is — until the next post says otherwise.
            pending.Status = PendingInventoryTransferStatuses.Approved;
            pending.LastError = null;
            await context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            if (acquired.RequestId is { } requestId)
            {
                try { await idempotencyRequestStore.ReleaseAsync(requestId, CancellationToken.None); }
                catch (Exception exception)
                {
                    logger.LogWarning(exception,
                        "Failed to release the post claim after editing pending inventory transfer {PendingId}", pending.Id);
                }
            }
        }

        var summary = string.Join("; ", changes);
        var reason = string.IsNullOrWhiteSpace(command.Reason) ? null : command.Reason.Trim();

        logger.LogInformation(
            "Lines of approved inventory transfer {PendingId} from {FromWarehouse} to {ToWarehouse} changed before posting: {Changes}",
            pending.Id, pending.FromWarehouse, pending.ToWarehouse, summary);

        try
        {
            await auditService.LogAsync(
                AuditActions.EditPendingTransferLines, "PendingInventoryTransfer", pending.Id.ToString(),
                $"Changed approved transfer {pending.DraftNumber ?? pending.Id.ToString()} from {pending.FromWarehouse} " +
                $"to {pending.ToWarehouse} before posting: {summary}" + (reason is null ? "" : $". Reason: {reason}"),
                true);
        }
        catch { }

        return new PendingInventoryTransferDecisionResponseDto
        {
            PendingTransferId = pending.Id,
            Status = pending.Status,
            ApprovalProcessComplete = true,
            Message = $"Saved {summary}. Post it to SAP when ready."
        };
    }

    /// <summary>
    /// The transfer's lines with <paramref name="edits"/> applied, and a line of text per change.
    /// </summary>
    internal static ErrorOr<(List<CreateInventoryTransferLineRequest> Lines, List<string> Changes)> ApplyEdits(
        CreateInventoryTransferRequest payload,
        IReadOnlyList<EditPendingTransferLineDto> edits)
    {
        var current = payload.Lines!;
        var problems = new List<string>();

        var wanted = new Dictionary<int, decimal>();
        foreach (var edit in edits)
        {
            if (edit.LineNum < 0 || edit.LineNum >= current.Count)
                problems.Add($"Line {edit.LineNum + 1} is not on this transfer.");
            else if (!wanted.TryAdd(edit.LineNum, edit.Quantity))
                problems.Add($"Line {edit.LineNum + 1} is named twice.");
        }

        var kept = new List<CreateInventoryTransferLineRequest>();
        var changes = new List<string>();
        for (var index = 0; index < current.Count; index++)
        {
            var line = current[index];
            if (!wanted.TryGetValue(index, out var quantity) || quantity == line.Quantity)
            {
                kept.Add(line);
                continue;
            }

            var label = $"Line {index + 1} ({line.ItemCode})";
            if (quantity < 0)
            {
                problems.Add($"{label}: a quantity cannot be negative; use 0 to take the line out.");
                continue;
            }

            if (quantity > line.Quantity)
            {
                // The approval was given for the quantity on the line. More than that is a request
                // nobody has approved, so it goes through as a new transfer.
                problems.Add(
                    $"{label}: {Format(quantity)} is more than the {Format(line.Quantity)} approved. " +
                    "Raise a new transfer for the extra.");
                continue;
            }

            if (quantity == 0)
            {
                changes.Add($"{line.ItemCode} {Format(line.Quantity)} → taken out");
                continue;
            }

            var fractional = UomQuantityValidation.BuildFractionalQuantityValidationError(
                index + 1, line.ItemCode, quantity, line.UoMCode);
            if (fractional is not null)
            {
                problems.Add(fractional);
                continue;
            }

            if (line.SerialNumbers is { Count: > 0 })
            {
                // Which serials stay behind cannot be chosen here, and SAP needs one per unit.
                problems.Add($"{label} carries serial numbers, so it can only be taken out or left whole.");
                continue;
            }

            kept.Add(WithQuantity(line, quantity));
            changes.Add($"{line.ItemCode} {Format(line.Quantity)} → {Format(quantity)}");
        }

        if (problems.Count > 0)
            return Errors.InventoryTransfer.ValidationFailed(string.Join(" ", problems));

        if (changes.Count == 0)
            return Errors.InventoryTransfer.ValidationFailed("No line was changed.");

        if (kept.Count == 0)
            return Errors.InventoryTransfer.ValidationFailed(
                "That takes out every line. Withdraw the transfer instead.");

        return (kept, changes);
    }

    /// <summary>
    /// A copy of the line at a lower quantity. A batch selection keeps its batches in order until it
    /// covers the new quantity, so the batches chosen first — the ones due out first — still go.
    /// </summary>
    private static CreateInventoryTransferLineRequest WithQuantity(CreateInventoryTransferLineRequest line, decimal quantity)
    {
        List<TransferBatchRequest>? batches = null;
        if (line.BatchNumbers is { Count: > 0 })
        {
            batches = [];
            var remaining = quantity;
            foreach (var batch in line.BatchNumbers)
            {
                if (remaining <= 0)
                    break;

                var take = Math.Min(batch.Quantity, remaining);
                batches.Add(new TransferBatchRequest { BatchNumber = batch.BatchNumber, Quantity = take });
                remaining -= take;
            }
        }

        return new CreateInventoryTransferLineRequest
        {
            ItemCode = line.ItemCode,
            Quantity = quantity,
            UoMCode = line.UoMCode,
            FromWarehouseCode = line.FromWarehouseCode,
            ToWarehouseCode = line.ToWarehouseCode,
            BatchNumbers = batches,
            SerialNumbers = line.SerialNumbers
        };
    }

    private static string Format(decimal quantity) => quantity.ToString("0.####", CultureInfo.InvariantCulture);
}
