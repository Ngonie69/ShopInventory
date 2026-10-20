using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.FactoryTransfers;

/// <summary>
/// Posts the two SAP legs of a factory transfer: out of the sender's warehouse into the holding
/// warehouse, and out of the holding warehouse to wherever the decision sends each unit.
/// </summary>
/// <remarks>
/// <para>
/// Every caller is past its commit point — the record already says Sending or Settling — so neither
/// method takes a <see cref="CancellationToken"/>. A browser closing mid-post must not strand the
/// stock half way; the SAP client's own timeout still bounds the call.
/// </para>
/// <para>
/// Two guards against a second document. The post lock, keyed on the transfer and leg and never on the
/// caller, stops two people posting the same leg at once. And each leg carries a deterministic
/// <c>U_OrderNumber</c>, so any attempt after the first looks the leg up in SAP before posting: a post
/// whose reply was lost is adopted, not repeated.
/// </para>
/// </remarks>
public interface IFactoryTransferLegPoster
{
    Task<ErrorOr<FactoryTransferEntity>> PostOutAsync(int transferId);

    Task<ErrorOr<FactoryTransferEntity>> PostInAsync(int transferId);
}

public sealed class FactoryTransferLegPoster(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IStockValidationService stockValidation,
    IIdempotencyRequestStore idempotencyRequestStore,
    ILogger<FactoryTransferLegPoster> logger) : IFactoryTransferLegPoster
{
    public const string IdempotencyScope = "factory-transfer-post";

    private const string OutLeg = "out";
    private const string InLeg = "in";

    /// <summary>
    /// How far before the send a leg's SAP document may be dated. SAP dates a transfer by its own
    /// business day, which can trail the UTC clock, so the lookup reaches back a little.
    /// </summary>
    private static readonly TimeSpan LookupSlack = TimeSpan.FromDays(2);

    public Task<ErrorOr<FactoryTransferEntity>> PostOutAsync(int transferId)
        => WithLockAsync(transferId, OutLeg, PostOutCoreAsync);

    public Task<ErrorOr<FactoryTransferEntity>> PostInAsync(int transferId)
        => WithLockAsync(transferId, InLeg, PostInCoreAsync);

    private async Task<ErrorOr<FactoryTransferEntity>> WithLockAsync(
        int transferId,
        string leg,
        Func<FactoryTransferEntity, Task<ErrorOr<FactoryTransferEntity>>> post)
    {
        var token = CancellationToken.None;
        var acquired = await idempotencyRequestStore.TryAcquireAsync<FactoryTransferLegReceipt>(
            IdempotencyScope, $"{transferId}:{leg}", new { TransferId = transferId, Leg = leg }, token);

        switch (acquired.Outcome)
        {
            case IdempotencyAcquireOutcome.ReplayAvailable:
                return await LoadAsync(transferId);
            case IdempotencyAcquireOutcome.InProgress:
                return Errors.FactoryTransfer.PostInProgress;
            case IdempotencyAcquireOutcome.RequestMismatch:
                return Errors.Idempotency.RequestMismatch("factory transfer post");
        }

        var requestId = acquired.RequestId!.Value;
        var release = true;
        try
        {
            var transfer = await LoadAsync(transferId);
            var result = await post(transfer);

            var docEntry = leg == OutLeg ? transfer.OutSapDocEntry : transfer.InSapDocEntry;
            if (!result.IsError && docEntry is int posted)
            {
                await idempotencyRequestStore.CompleteAsync(requestId, new FactoryTransferLegReceipt(posted), token);
                release = false;
            }

            return result;
        }
        finally
        {
            if (release)
            {
                try { await idempotencyRequestStore.ReleaseAsync(requestId, token); }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Failed to release the post lock for factory transfer {TransferId} {Leg}", transferId, leg);
                }
            }
        }
    }

    private async Task<ErrorOr<FactoryTransferEntity>> PostOutCoreAsync(FactoryTransferEntity transfer)
    {
        if (transfer.Status != FactoryTransferStatuses.Sending)
            return transfer.OutSapDocEntry is null
                ? Errors.FactoryTransfer.NotActionable(transfer.Status)
                : transfer;

        transfer.OutAttempts++;
        transfer.LastAttemptedAtUtc = DateTime.UtcNow;
        await SaveAsync();

        var orderedLines = transfer.Lines.OrderBy(line => line.LineNum).ToList();
        var payload = new CreateInventoryTransferRequest
        {
            FromWarehouse = transfer.SourceWarehouseCode,
            ToWarehouse = transfer.HoldingWarehouseCode,
            ClientRequestId = transfer.OutOrderNumber,
            OrderNumber = transfer.OutOrderNumber,
            FinalDestinationWarehouse = transfer.DestinationWarehouseCode,
            Comments = FactoryTransferRemarks.ForOutLeg(transfer),
            Lines = orderedLines.Select(line => new CreateInventoryTransferLineRequest
            {
                ItemCode = line.ItemCode,
                Quantity = line.Quantity,
                UoMCode = line.UoMCode,
                FromWarehouseCode = transfer.SourceWarehouseCode,
                ToWarehouseCode = transfer.HoldingWarehouseCode
            }).ToList()
        };

        var outcome = await PostLegAsync(transfer, transfer.OutOrderNumber, transfer.OutAttempts, payload);
        if (outcome.IsError)
        {
            transfer.Status = FactoryTransferStatuses.SendFailed;
            transfer.LastError = Clip(outcome.FirstError.Description);
            await SaveAsync();
            return outcome.Errors;
        }

        var document = outcome.Value;
        RecordOutBatches(orderedLines, document);
        transfer.OutSapDocEntry = document.DocEntry;
        transfer.OutSapDocNum = document.DocNum;
        transfer.OutPostedAtUtc = DateTime.UtcNow;
        transfer.Status = FactoryTransferStatuses.InTransit;
        transfer.LastError = null;
        await SaveAsync();

        logger.LogInformation(
            "Factory transfer {Number} sent {From} -> {Holding} for {To}. DocEntry {DocEntry}, DocNum {DocNum}",
            transfer.Number, transfer.SourceWarehouseCode, transfer.HoldingWarehouseCode,
            transfer.DestinationWarehouseCode, document.DocEntry, document.DocNum);

        return transfer;
    }

    private async Task<ErrorOr<FactoryTransferEntity>> PostInCoreAsync(FactoryTransferEntity transfer)
    {
        if (transfer.Status != FactoryTransferStatuses.Settling)
            return transfer.InSapDocEntry is null
                ? Errors.FactoryTransfer.NotActionable(transfer.Status)
                : transfer;

        transfer.InAttempts++;
        transfer.LastAttemptedAtUtc = DateTime.UtcNow;
        await SaveAsync();

        var lines = BuildInLines(transfer);
        var payload = new CreateInventoryTransferRequest
        {
            FromWarehouse = transfer.HoldingWarehouseCode,
            ToWarehouse = transfer.Decision == FactoryTransferDecisions.Accepted
                ? transfer.DestinationWarehouseCode
                : transfer.SourceWarehouseCode,
            ClientRequestId = transfer.InOrderNumber,
            OrderNumber = transfer.InOrderNumber,
            Comments = FactoryTransferRemarks.ForInLeg(transfer),
            Lines = lines
        };

        var outcome = await PostLegAsync(transfer, transfer.InOrderNumber, transfer.InAttempts, payload);
        if (outcome.IsError)
        {
            transfer.Status = FactoryTransferStatuses.SettleFailed;
            transfer.LastError = Clip(outcome.FirstError.Description);
            await SaveAsync();
            return outcome.Errors;
        }

        var document = outcome.Value;
        transfer.InSapDocEntry = document.DocEntry;
        transfer.InSapDocNum = document.DocNum;
        transfer.InPostedAtUtc = DateTime.UtcNow;
        transfer.Status = SettledStatus(transfer);
        transfer.LastError = null;
        await SaveAsync();

        logger.LogInformation(
            "Factory transfer {Number} settled as {Status} out of {Holding}. DocEntry {DocEntry}, DocNum {DocNum}",
            transfer.Number, transfer.Status, transfer.HoldingWarehouseCode, document.DocEntry, document.DocNum);

        return transfer;
    }

    /// <summary>
    /// Finds the leg in SAP if an earlier attempt may have made it, otherwise checks stock and posts it.
    /// </summary>
    private async Task<ErrorOr<InventoryTransfer>> PostLegAsync(
        FactoryTransferEntity transfer,
        string orderNumber,
        int attempt,
        CreateInventoryTransferRequest payload)
    {
        var token = CancellationToken.None;
        var since = transfer.SentAtUtc - LookupSlack;

        if (attempt > 1)
        {
            try
            {
                var existing = await sapClient.FindStockTransferByOrderNumberAsync(orderNumber, since, token);
                if (existing is not null)
                {
                    logger.LogWarning(
                        "Factory transfer leg {OrderNumber} was already in SAP as DocEntry {DocEntry}; adopting it instead of posting again",
                        orderNumber, existing.DocEntry);
                    return existing;
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not look up factory transfer leg {OrderNumber} in SAP", orderNumber);
                return Errors.FactoryTransfer.PostFailed(
                    "Could not check SAP for an earlier attempt at this transfer, so nothing was posted. Retry once SAP is answering.");
            }
        }

        StockValidationResult stock;
        try
        {
            stock = await stockValidation.ValidateInventoryTransferStockAsync(payload, token);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not check stock for factory transfer leg {OrderNumber}", orderNumber);
            return Errors.FactoryTransfer.PostFailed($"Could not read stock from SAP, so nothing was posted: {exception.Message}");
        }

        if (!stock.IsValid)
        {
            return Errors.FactoryTransfer.InsufficientStock(
                $"{payload.FromWarehouse} does not hold enough stock in SAP: "
                + string.Join("; ", stock.Errors.Select(error => error.Message)));
        }

        if (!stock.StockWasFullyRead)
        {
            return Errors.FactoryTransfer.PostFailed(
                "Could not read stock from SAP for warehouse(s) " + string.Join(", ", stock.UnreadableWarehouses)
                + ". Nothing was posted; retry once SAP is answering.");
        }

        try
        {
            return await sapClient.CreateInventoryTransferAsync(payload, stock.PreFetchedData, token);
        }
        catch (OperationCanceledException exception)
        {
            // Nothing cancels this token, so this is the SAP client's own timeout: the request reached
            // SAP and no answer came back. The retry looks the leg up by its order number first.
            logger.LogError(exception, "The SAP post of factory transfer leg {OrderNumber} timed out; outcome unknown", orderNumber);
            return Errors.FactoryTransfer.PostFailed(
                "SAP did not answer in time, so it is not known whether the stock moved. "
                + "Retry: it looks for this transfer in SAP before posting anything.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "SAP refused factory transfer leg {OrderNumber}", orderNumber);
            return Errors.FactoryTransfer.PostFailed($"SAP refused the transfer: {exception.Message}");
        }
    }

    /// <summary>
    /// The in leg moves out of the holding warehouse exactly the batches the out leg put in, split
    /// between the destination (accepted) and the sender's warehouse (the rest).
    /// </summary>
    internal static List<CreateInventoryTransferLineRequest> BuildInLines(FactoryTransferEntity transfer)
    {
        var lines = new List<CreateInventoryTransferLineRequest>();
        foreach (var line in transfer.Lines.OrderBy(item => item.LineNum))
        {
            var accepted = transfer.Decision == FactoryTransferDecisions.Accepted ? line.AcceptedQuantity ?? 0 : 0;
            var returned = line.Quantity - accepted;
            var batches = ReadBatches(line);

            var (acceptedBatches, returnedBatches) = SplitBatches(batches, accepted);

            if (accepted > 0)
                lines.Add(InLine(transfer, line, accepted, transfer.DestinationWarehouseCode, acceptedBatches));
            if (returned > 0)
                lines.Add(InLine(transfer, line, returned, transfer.SourceWarehouseCode, returnedBatches));
        }

        return lines;
    }

    private static CreateInventoryTransferLineRequest InLine(
        FactoryTransferEntity transfer,
        FactoryTransferLineEntity line,
        decimal quantity,
        string toWarehouse,
        List<TransferBatchRequest>? batches) => new()
    {
        ItemCode = line.ItemCode,
        Quantity = quantity,
        UoMCode = line.UoMCode,
        FromWarehouseCode = transfer.HoldingWarehouseCode,
        ToWarehouseCode = toWarehouse,
        BatchNumbers = batches
    };

    /// <summary>
    /// Takes <paramref name="accepted"/> units from the front of the batch list and leaves the rest.
    /// Null for a line SAP moved without batches, so the client's own handling applies.
    /// </summary>
    internal static (List<TransferBatchRequest>? Accepted, List<TransferBatchRequest>? Returned) SplitBatches(
        IReadOnlyList<FactoryTransferBatch> batches,
        decimal accepted)
    {
        if (batches.Count == 0)
            return (null, null);

        var taken = new List<TransferBatchRequest>();
        var left = new List<TransferBatchRequest>();
        var remaining = accepted;

        foreach (var batch in batches)
        {
            var take = Math.Min(remaining, batch.Quantity);
            if (take > 0)
                taken.Add(new TransferBatchRequest { BatchNumber = batch.BatchNumber, Quantity = take });
            if (batch.Quantity - take > 0)
                left.Add(new TransferBatchRequest { BatchNumber = batch.BatchNumber, Quantity = batch.Quantity - take });
            remaining -= take;
        }

        return (taken, left);
    }

    private static void RecordOutBatches(List<FactoryTransferLineEntity> orderedLines, InventoryTransfer document)
    {
        var documentLines = (document.StockTransferLines ?? []).OrderBy(line => line.LineNum).ToList();
        for (var index = 0; index < orderedLines.Count && index < documentLines.Count; index++)
        {
            var batches = (documentLines[index].BatchNumbers ?? [])
                .Where(batch => !string.IsNullOrWhiteSpace(batch.BatchNumber) && batch.Quantity > 0)
                .Select(batch => new FactoryTransferBatch(batch.BatchNumber!, batch.Quantity))
                .ToList();
            orderedLines[index].BatchesJson = batches.Count == 0 ? null : JsonSerializer.Serialize(batches);
        }
    }

    internal static IReadOnlyList<FactoryTransferBatch> ReadBatches(FactoryTransferLineEntity line)
    {
        if (string.IsNullOrWhiteSpace(line.BatchesJson))
            return [];
        try { return JsonSerializer.Deserialize<List<FactoryTransferBatch>>(line.BatchesJson) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string SettledStatus(FactoryTransferEntity transfer) => transfer.Decision switch
    {
        FactoryTransferDecisions.Rejected => FactoryTransferStatuses.Rejected,
        FactoryTransferDecisions.Recalled => FactoryTransferStatuses.Recalled,
        _ => transfer.Lines.All(line => line.AcceptedQuantity == line.Quantity)
            ? FactoryTransferStatuses.Accepted
            : FactoryTransferStatuses.PartiallyAccepted
    };

    private async Task<FactoryTransferEntity> LoadAsync(int transferId)
    {
        // Reload rather than trust a tracked copy: the claim that put the record in Sending or
        // Settling was a conditional ExecuteUpdate, which never touches the change tracker.
        var tracked = context.ChangeTracker.Entries<FactoryTransferEntity>()
            .FirstOrDefault(entry => entry.Entity.Id == transferId);
        if (tracked is not null)
        {
            await tracked.ReloadAsync();
            foreach (var line in tracked.Entity.Lines)
                await context.Entry(line).ReloadAsync();
        }

        return await context.FactoryTransfers
            .AsTracking()
            .Include(item => item.Lines)
            .SingleAsync(item => item.Id == transferId);
    }

    private Task SaveAsync() => context.SaveChangesAsync(CancellationToken.None);

    private static string Clip(string message) => message.Length > 2000 ? message[..2000] : message;
}

/// <summary>One batch on a factory transfer line, as SAP allocated it on the out leg.</summary>
public sealed record FactoryTransferBatch(string BatchNumber, decimal Quantity);

/// <summary>What a completed post lock remembers.</summary>
public sealed record FactoryTransferLegReceipt(int DocEntry);
