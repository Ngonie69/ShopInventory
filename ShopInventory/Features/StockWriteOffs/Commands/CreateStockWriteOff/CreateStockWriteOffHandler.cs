using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Common.Validation;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.StockWriteOffs.Commands.CreateStockWriteOff;

/// <summary>
/// Records a write-off and issues the stock out of SAP in one go.
/// </summary>
/// <remarks>
/// <para>
/// Single-step by design: the person counting is the person committing, and the record is written
/// before the goods issue so that a post which fails, or whose reply is lost, leaves something to act
/// on rather than nothing at all.
/// </para>
/// <para>
/// Three guards, each covering what the others cannot. <see cref="StockWriteOffEntity.ClientRequestId"/>
/// makes a resent submit find the write-off it already raised. The post lock
/// (<see cref="IIdempotencyRequestStore"/>, keyed on the record alone and never on the caller) stops
/// two people posting one write-off. And the <see cref="StockWriteOffStatuses.Posted"/> check is the
/// lasting guard, because the lock row expires within the hour while the record does not.
/// </para>
/// <para>
/// Once the record is claimed the request's token is dropped: from there the goods issue is an
/// obligation, and a closed browser tab must not leave stock half written off.
/// </para>
/// </remarks>
public sealed class CreateStockWriteOffHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IStockValidationService stockValidation,
    IIdempotencyRequestStore idempotencyRequestStore,
    IAuditService auditService,
    IOptions<SAPSettings> sapSettings,
    IOptions<StockWriteOffSettings> writeOffSettings,
    ILogger<CreateStockWriteOffHandler> logger)
    : IRequestHandler<CreateStockWriteOffCommand, ErrorOr<StockWriteOffResultDto>>
{
    public const string IdempotencyScope = "stock-write-off-post";

    public async Task<ErrorOr<StockWriteOffResultDto>> Handle(
        CreateStockWriteOffCommand command,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.StockWriteOff.SapDisabled;

        var request = command.Request;

        var warehouse = request.WarehouseCode?.Trim();
        if (string.IsNullOrWhiteSpace(warehouse))
            return Errors.StockWriteOff.WarehouseRequired;

        if (request.Lines is null || request.Lines.Count == 0)
            return Errors.StockWriteOff.NothingToWriteOff;

        var userName = await StockWriteOffActor.ResolveNameAsync(context, command.UserId, cancellationToken);
        if (userName is null)
            return Errors.StockWriteOff.UserNotFound;

        var reasonCheck = await CheckReasonAsync(request.Reason, cancellationToken);
        if (reasonCheck.IsError)
            return reasonCheck.Errors;

        var warehouseCheck = await CheckWarehouseAsync(warehouse, cancellationToken);
        if (warehouseCheck.IsError)
            return warehouseCheck.Errors;

        // Quantities and units settled before anything is persisted, so a record is never written for
        // a document that could never have been built.
        var quantityErrors = await UomQuantityValidation.ValidateAndNormalizeLineQuantitiesAsync(
            context,
            request.Lines,
            line => line.ItemCode,
            line => line.Quantity,
            line => line.UoMCode,
            (line, uomCode) => line.UoMCode = uomCode,
            cancellationToken);
        if (quantityErrors.Count > 0)
            return Errors.StockWriteOff.LinesInvalid(string.Join("; ", quantityErrors));

        var existing = await FindOrCreateAsync(command, warehouse, userName, cancellationToken);
        if (existing.IsError)
            return existing.Errors;

        var writeOff = existing.Value;

        if (writeOff.Status == StockWriteOffStatuses.Posted)
            return await AlreadyPostedAsync(writeOff.Id);

        if (!StockWriteOffStatuses.MayPost(writeOff.Status))
            return Errors.StockWriteOff.NotActionable(writeOff.Status);

        var acquired = await idempotencyRequestStore.TryAcquireAsync<StockWriteOffPostReceipt>(
            IdempotencyScope,
            writeOff.Id.ToString(),
            new { WriteOffId = writeOff.Id },
            cancellationToken);

        switch (acquired.Outcome)
        {
            case IdempotencyAcquireOutcome.ReplayAvailable:
                return await AlreadyPostedAsync(writeOff.Id);
            case IdempotencyAcquireOutcome.InProgress:
                return Errors.StockWriteOff.PostInProgress;
            case IdempotencyAcquireOutcome.RequestMismatch:
                return Errors.Idempotency.RequestMismatch("stock write-off");
        }

        var requestId = acquired.RequestId!.Value;
        var release = true;
        try
        {
            var result = await ClaimAndPostAsync(writeOff.Id, request.DocDate, cancellationToken);
            if (!result.IsError && result.Value.WriteOff.SapDocEntry is int docEntry)
            {
                await idempotencyRequestStore.CompleteAsync(
                    requestId,
                    new StockWriteOffPostReceipt(docEntry, result.Value.WriteOff.SapDocNum),
                    CancellationToken.None);
                release = false;
            }

            return result;
        }
        finally
        {
            if (release)
            {
                try { await idempotencyRequestStore.ReleaseAsync(requestId, CancellationToken.None); }
                catch (Exception exception)
                {
                    logger.LogWarning(exception,
                        "Failed to release the post lock for stock write-off {WriteOffId}", writeOff.Id);
                }
            }
        }
    }

    private async Task<ErrorOr<StockWriteOffResultDto>> ClaimAndPostAsync(
        int writeOffId,
        string? docDate,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var claimed = await context.StockWriteOffs
            .Where(row => row.Id == writeOffId
                && (row.Status == StockWriteOffStatuses.Pending
                    || row.Status == StockWriteOffStatuses.PostFailed
                    || row.Status == StockWriteOffStatuses.Posting))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, StockWriteOffStatuses.Posting)
                .SetProperty(row => row.LastAttemptedAtUtc, now),
                cancellationToken);

        if (claimed == 0)
        {
            var moved = await ReadDetailAsync(writeOffId, cancellationToken);
            return moved is null
                ? Errors.StockWriteOff.NotFound(writeOffId)
                : Errors.StockWriteOff.NotActionable(moved.Status);
        }

        // Past the claim: nothing downstream may be cancelled by the caller going away.
        var token = CancellationToken.None;

        // Loaded after the claim, so the tracked copy is the claimed row rather than a stale one the
        // conditional update wrote around.
        var writeOff = await context.StockWriteOffs
            .AsTracking()
            .Include(row => row.Lines)
            .SingleAsync(row => row.Id == writeOffId, token);

        var payload = BuildGoodsIssueRequest(writeOff, docDate);

        try
        {
            // The stock check the transfer path uses, asked about one side. It reads only the source
            // warehouse of the request it is given, so a write-off is a transfer with no destination
            // as far as it is concerned, and it comes back having read exactly what a goods issue
            // needs to know.
            var stock = await stockValidation.ValidateInventoryTransferStockAsync(
                AsSourceOnlyTransfer(writeOff),
                token);

            if (!stock.IsValid)
            {
                var message = $"Warehouse {writeOff.WarehouseCode} does not hold enough stock in SAP: "
                    + string.Join("; ", stock.Errors.Select(error => error.Message));
                await FailAsync(writeOff, message);
                return Errors.StockWriteOff.InsufficientStock(message);
            }

            if (!stock.StockWasFullyRead)
            {
                // Unread is not the same as short, and the difference decides whether stock may be
                // destroyed. Fail closed.
                var message = "Could not read stock from SAP for warehouse(s) "
                    + string.Join(", ", stock.UnreadableWarehouses)
                    + ". Nothing was written off; post again once SAP is answering.";
                await FailAsync(writeOff, message);
                return Errors.StockWriteOff.PostFailed(message);
            }

            var issued = await sapClient.CreateGoodsIssueAsync(payload, token);

            writeOff.Status = StockWriteOffStatuses.Posted;
            writeOff.SapDocEntry = issued.DocEntry;
            writeOff.SapDocNum = issued.DocNum;
            writeOff.SapReference = payload.SapReference;
            writeOff.PostedAtUtc = DateTime.UtcNow;
            writeOff.LastError = null;
            await context.SaveChangesAsync(token);

            logger.LogInformation(
                "Stock write-off {WriteOffId} issued out of {Warehouse}. DocEntry {DocEntry}, DocNum {DocNum}",
                writeOff.Id, writeOff.WarehouseCode, issued.DocEntry, issued.DocNum);

            await AuditAsync(writeOff, issued.DocNum);

            return new StockWriteOffResultDto
            {
                Message = $"Written off. Goods issue #{issued.DocNum} took {writeOff.Lines.Count} line(s) "
                    + $"out of {writeOff.WarehouseCode}.",
                WriteOff = (await ReadDetailAsync(writeOff.Id, token))!
            };
        }
        catch (OperationCanceledException exception)
        {
            // Nothing cancels this token, so this is the SAP client's own timeout: the request reached
            // SAP and no answer came back. The goods issue may exist, so the record says so rather
            // than inviting a retry that would write the stock off twice.
            logger.LogError(exception,
                "The SAP goods issue for stock write-off {WriteOffId} timed out; the outcome is unknown",
                writeOff.Id);
            const string message = "SAP did not answer in time, so it is not known whether the stock was written "
                + "off. Check SAP for this goods issue before posting again — posting again will issue it again.";
            await FailAsync(writeOff, message);
            return Errors.StockWriteOff.PostFailed(message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to post stock write-off {WriteOffId} to SAP", writeOff.Id);
            await FailAsync(writeOff, exception.Message);
            return Errors.StockWriteOff.PostFailed($"SAP refused the write-off: {exception.Message}");
        }
    }

    /// <summary>
    /// Finds the write-off this request already raised, or raises it.
    /// </summary>
    /// <remarks>
    /// The unique index on <see cref="StockWriteOffEntity.ClientRequestId"/> is the real guard: two
    /// submits racing each other both miss the read, and the loser's insert is what tells it so.
    /// </remarks>
    private async Task<ErrorOr<StockWriteOffEntity>> FindOrCreateAsync(
        CreateStockWriteOffCommand command,
        string warehouse,
        string userName,
        CancellationToken cancellationToken)
    {
        var request = command.Request;
        var clientRequestId = string.IsNullOrWhiteSpace(request.ClientRequestId)
            ? Guid.NewGuid().ToString("N")
            : request.ClientRequestId.Trim();

        var existing = await context.StockWriteOffs
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.ClientRequestId == clientRequestId, cancellationToken);

        if (existing is not null)
        {
            return existing.RaisedByUserId == command.UserId
                ? existing
                : Errors.StockWriteOff.DuplicateRequestFromAnotherUser;
        }

        var writeOff = new StockWriteOffEntity
        {
            ClientRequestId = clientRequestId,
            RaisedByUserId = command.UserId,
            RaisedByName = userName,
            WarehouseCode = warehouse,
            Reason = request.Reason.Trim(),
            Remarks = string.IsNullOrWhiteSpace(request.Remarks) ? null : request.Remarks.Trim(),
            CreatedAtUtc = DateTime.UtcNow,
            Status = StockWriteOffStatuses.Pending,
            Lines = request.Lines
                .Select((line, index) => new StockWriteOffLineEntity
                {
                    LineNum = index + 1,
                    ItemCode = UomQuantityValidation.NormalizeItemCode(line.ItemCode) ?? line.ItemCode.Trim(),
                    ItemDescription = line.ItemDescription,
                    Quantity = line.Quantity,
                    UoMCode = line.UoMCode,
                    BatchNumber = string.IsNullOrWhiteSpace(line.BatchNumber) ? null : line.BatchNumber.Trim(),
                    SerialNumber = string.IsNullOrWhiteSpace(line.SerialNumber) ? null : line.SerialNumber.Trim()
                })
                .ToList()
        };

        context.StockWriteOffs.Add(writeOff);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Lost the race on the unique index. The winner's row is the write-off this request
            // raised, so adopt it rather than reporting a failure the caller cannot act on.
            context.Entry(writeOff).State = EntityState.Detached;
            foreach (var line in writeOff.Lines)
                context.Entry(line).State = EntityState.Detached;

            var winner = await context.StockWriteOffs
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.ClientRequestId == clientRequestId, cancellationToken);

            if (winner is null)
                throw;

            return winner.RaisedByUserId == command.UserId
                ? winner
                : Errors.StockWriteOff.DuplicateRequestFromAnotherUser;
        }

        return writeOff;
    }

    /// <summary>
    /// The reason must be one SAP will accept where SAP defines the list, because SAP rejects a value
    /// its own field does not carry and the refusal arrives only when the document is posted.
    /// </summary>
    private async Task<ErrorOr<Success>> CheckReasonAsync(string reason, CancellationToken cancellationToken)
    {
        var trimmed = reason.Trim();

        IReadOnlyList<SapDocumentLineReason> sapReasons;
        try
        {
            sapReasons = await sapClient.GetGoodsIssueLineReasonsAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // SAP not answering must not stop a write-off being recorded against the configured list.
            logger.LogWarning(exception, "Could not read the goods issue reasons from SAP; using the configured list");
            sapReasons = Array.Empty<SapDocumentLineReason>();
        }

        var allowed = sapReasons.Count > 0
            ? sapReasons.Select(row => row.Value).ToList()
            : writeOffSettings.Value.Reasons;

        // Nothing offering a list is not the same as offering an empty one. SAP defines no reason
        // field on this company database and the settings name no reasons either, so there is no
        // list for the reason to be wrong against — refusing every write-off over a missing setting
        // would be worse than recording the words the operator typed.
        if (allowed.Count == 0)
        {
            return Result.Success;
        }

        return allowed.Any(value => string.Equals(value, trimmed, StringComparison.OrdinalIgnoreCase))
            ? Result.Success
            : Errors.StockWriteOff.UnknownReason(trimmed);
    }

    private async Task<ErrorOr<Success>> CheckWarehouseAsync(string warehouse, CancellationToken cancellationToken)
    {
        try
        {
            var warehouses = await sapClient.GetWarehousesAsync(cancellationToken);
            if (warehouses.Count == 0)
                return Result.Success;

            return warehouses.Any(row => string.Equals(row.WarehouseCode, warehouse, StringComparison.OrdinalIgnoreCase))
                ? Result.Success
                : Errors.StockWriteOff.UnknownWarehouse(warehouse);
        }
        catch (Exception exception)
        {
            // A warehouse this system cannot list is still a warehouse SAP will judge for itself when
            // the document is posted, so an unreadable list does not stop the write-off here.
            logger.LogWarning(exception, "Could not read the SAP warehouses to check {Warehouse}", warehouse);
            return Result.Success;
        }
    }

    private CreateGoodsIssueRequest BuildGoodsIssueRequest(StockWriteOffEntity writeOff, string? docDate)
    {
        return new CreateGoodsIssueRequest
        {
            WarehouseCode = writeOff.WarehouseCode,
            DocDate = docDate,
            Comments = BuildComments(writeOff),
            JournalMemo = writeOffSettings.Value.JournalMemo,
            ClientRequestId = writeOff.ClientRequestId,
            SapReference = $"write-off-{writeOff.Id}",
            Lines = writeOff.Lines
                .OrderBy(line => line.LineNum)
                .Select(line => new CreateGoodsIssueLineRequest
                {
                    ItemCode = line.ItemCode,
                    Quantity = line.Quantity,
                    UoMCode = line.UoMCode,
                    // The reason is the header's, on every line, because SAP keeps it on the line —
                    // the same reason a cancellation credit note writes it to all of them.
                    Reason = writeOff.Reason,
                    // One batch per line, because a line is one thing counted. SAP takes a list, and
                    // the list adding up to the line quantity is what it checks.
                    BatchNumbers = line.BatchNumber is null
                        ? null
                        : [new TransferBatchRequest { BatchNumber = line.BatchNumber, Quantity = line.Quantity }],
                    SerialNumbers = line.SerialNumber is null
                        ? null
                        : [new TransferSerialRequest { InternalSerialNumber = line.SerialNumber, Quantity = 1 }]
                })
                .ToList()
        };
    }

    /// <summary>
    /// The write-off as a one-sided transfer, purely so the existing stock check can read it.
    /// </summary>
    /// <remarks>
    /// <c>ToWarehouse</c> is left null on purpose:
    /// <see cref="IStockValidationService.ValidateInventoryTransferStockAsync"/> reads only the source
    /// side of what it is given, and naming a destination here would invent one. If that method ever
    /// starts reading a destination, <c>StockWriteOffTests</c> is where it will be caught.
    /// </remarks>
    private static CreateInventoryTransferRequest AsSourceOnlyTransfer(StockWriteOffEntity writeOff)
    {
        return new CreateInventoryTransferRequest
        {
            FromWarehouse = writeOff.WarehouseCode,
            ClientRequestId = writeOff.ClientRequestId,
            Lines = writeOff.Lines
                .OrderBy(line => line.LineNum)
                .Select(line => new CreateInventoryTransferLineRequest
                {
                    ItemCode = line.ItemCode,
                    Quantity = line.Quantity,
                    UoMCode = line.UoMCode,
                    FromWarehouseCode = writeOff.WarehouseCode,
                    BatchNumbers = line.BatchNumber is null
                        ? null
                        : [new TransferBatchRequest { BatchNumber = line.BatchNumber, Quantity = line.Quantity }],
                    SerialNumbers = line.SerialNumber is null
                        ? null
                        : [new TransferSerialRequest { InternalSerialNumber = line.SerialNumber, Quantity = 1 }]
                })
                .ToList()
        };
    }

    private static string BuildComments(StockWriteOffEntity writeOff)
    {
        var comments = $"Write-off #{writeOff.Id} out of {writeOff.WarehouseCode} — {writeOff.Reason}. "
            + $"Raised by {writeOff.RaisedByName}.";

        if (!string.IsNullOrWhiteSpace(writeOff.Remarks))
            comments += $" {writeOff.Remarks.Trim()}";

        return comments.Length <= SapCommentsLength ? comments : comments[..SapCommentsLength];
    }

    /// <summary>SAP's Comments column on a document.</summary>
    private const int SapCommentsLength = 254;

    private async Task FailAsync(StockWriteOffEntity writeOff, string message)
    {
        writeOff.Status = StockWriteOffStatuses.PostFailed;
        writeOff.LastError = message.Length > 2000 ? message[..2000] : message;
        await context.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<ErrorOr<StockWriteOffResultDto>> AlreadyPostedAsync(int writeOffId)
    {
        var detail = await ReadDetailAsync(writeOffId, CancellationToken.None);
        if (detail is null)
            return Errors.StockWriteOff.NotFound(writeOffId);

        return new StockWriteOffResultDto
        {
            AlreadyPosted = true,
            Message = detail.SapDocNum is int docNum
                ? $"Already written off by goods issue #{docNum}."
                : "Already written off.",
            WriteOff = detail
        };
    }

    private Task<StockWriteOffDetailDto?> ReadDetailAsync(int writeOffId, CancellationToken cancellationToken)
    {
        return context.StockWriteOffs
            .AsNoTracking()
            .Where(row => row.Id == writeOffId)
            .Select(StockWriteOffProjections.Detail)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task AuditAsync(StockWriteOffEntity writeOff, int docNum)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.CreateStockWriteOff, "StockWriteOff", writeOff.Id.ToString(),
                $"Write-off #{writeOff.Id} ({writeOff.Reason}) issued {writeOff.Lines.Sum(line => line.Quantity)} "
                + $"unit(s) out of {writeOff.WarehouseCode} as goods issue #{docNum}",
                true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not audit stock write-off {WriteOffId}", writeOff.Id);
        }
    }
}

/// <summary>
/// What a completed post is remembered by, so a replay can answer without going back to SAP.
/// </summary>
public sealed record StockWriteOffPostReceipt(int DocEntry, int? DocNum);
