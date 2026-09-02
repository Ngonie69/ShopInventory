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

namespace ShopInventory.Features.InventoryTransfers.Commands.CreateInventoryTransfer;

/// <summary>
/// Accepts a direct inventory transfer and holds it locally until the approval process completes.
/// Nothing is posted to SAP here; the pending-transfer poster runs only after every required stage
/// has approved it.
/// </summary>
public sealed class CreateInventoryTransferHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IStockValidationService stockValidation,
    IInventoryTransferApprovalService approvalService,
    IAuditService auditService,
    IIdempotencyRequestStore idempotencyRequestStore,
    IOptions<SAPSettings> settings,
    ILogger<CreateInventoryTransferHandler> logger
) : IRequestHandler<CreateInventoryTransferCommand, ErrorOr<InventoryTransferCreatedResponseDto>>
{
    public async Task<ErrorOr<InventoryTransferCreatedResponseDto>> Handle(
        CreateInventoryTransferCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.InventoryTransfer.SapDisabled;

        var request = command.Request;

        var quantityErrors = await ValidateTransferQuantitiesAsync(request, cancellationToken);
        if (quantityErrors.Count > 0)
        {
            logger.LogWarning("Transfer quantity validation failed: {Errors}", string.Join(", ", quantityErrors));
            return Errors.InventoryTransfer.ValidationFailed(
                $"Quantity validation failed: {string.Join("; ", quantityErrors)}");
        }

        var idempotencyKey = string.IsNullOrWhiteSpace(request.ClientRequestId)
            ? null
            : request.ClientRequestId.Trim();

        if (idempotencyKey is null)
            return await HandleCoreAsync(command, null, cancellationToken);

        // A resubmission of an already-held transfer must return the same pending record
        // rather than opening a second approval for the same stock movement.
        var alreadyHeld = await context.PendingInventoryTransfers
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.ClientRequestId == idempotencyKey, cancellationToken);
        if (alreadyHeld is not null)
            return BuildSubmittedResponse(alreadyHeld, replayed: true);

        long? idempotencyRequestId = null;
        var releaseIdempotencyRequest = false;
        try
        {
            var acquireResult = await idempotencyRequestStore.TryAcquireAsync<InventoryTransferCreatedResponseDto>(
                "inventorytransfers.create",
                idempotencyKey,
                request,
                cancellationToken);

            switch (acquireResult.Outcome)
            {
                case IdempotencyAcquireOutcome.ReplayAvailable when acquireResult.Response is not null:
                    logger.LogWarning("Replaying inventory transfer submission for idempotency key {Key}", idempotencyKey);
                    return acquireResult.Response;
                case IdempotencyAcquireOutcome.InProgress:
                    return Errors.Idempotency.RequestInProgress("inventory transfer creation");
                case IdempotencyAcquireOutcome.RequestMismatch:
                    return Errors.Idempotency.RequestMismatch("inventory transfer creation");
                case IdempotencyAcquireOutcome.Acquired:
                    idempotencyRequestId = acquireResult.RequestId;
                    releaseIdempotencyRequest = true;
                    break;
            }

            var result = await HandleCoreAsync(command, idempotencyKey, cancellationToken);

            if (idempotencyRequestId.HasValue && !result.IsError)
            {
                try
                {
                    await idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, result.Value, cancellationToken);
                    releaseIdempotencyRequest = false;
                }
                catch (Exception completeException)
                {
                    logger.LogWarning(completeException, "Failed to persist inventory transfer idempotency completion for request {RequestId}", idempotencyRequestId.Value);
                }
            }

            return result;
        }
        finally
        {
            if (releaseIdempotencyRequest && idempotencyRequestId.HasValue)
            {
                try
                {
                    await idempotencyRequestStore.ReleaseAsync(idempotencyRequestId.Value, CancellationToken.None);
                }
                catch (Exception releaseException)
                {
                    logger.LogWarning(releaseException, "Failed to release inventory transfer idempotency request {RequestId}", idempotencyRequestId.Value);
                }
            }
        }
    }

    private async Task<ErrorOr<InventoryTransferCreatedResponseDto>> HandleCoreAsync(
        CreateInventoryTransferCommand command,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var request = command.Request;

        var submitter = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == command.UserId && user.IsActive, cancellationToken);
        if (submitter is null)
            return Errors.InventoryTransfer.ApproverNotAuthenticated;

        if (string.IsNullOrWhiteSpace(request.FromWarehouse))
            return Errors.InventoryTransfer.ValidationFailed("A source warehouse is required.");
        if (string.IsNullOrWhiteSpace(request.ToWarehouse))
            return Errors.InventoryTransfer.ValidationFailed("A destination warehouse is required.");

        // Catch obviously bad submissions now so an approver is not asked to sign off on a
        // transfer that could never post. Both checks run again at post time, which is
        // authoritative — stock can move while the transfer waits for approval.
        var warehouseErrors = await ValidateWarehouseCodesAsync(request, cancellationToken);
        if (warehouseErrors is { Count: > 0 })
        {
            logger.LogWarning("Invalid warehouse codes in transfer submission: {Errors}", string.Join(", ", warehouseErrors));
            return Errors.InventoryTransfer.InvalidWarehouse(string.Join("; ", warehouseErrors));
        }

        var stockError = await ValidateStockBestEffortAsync(request, cancellationToken);
        if (stockError is not null)
            return stockError.Value;

        var pending = new PendingInventoryTransferEntity
        {
            ClientRequestId = idempotencyKey,
            FromWarehouse = request.FromWarehouse!.Trim(),
            ToWarehouse = request.ToWarehouse!.Trim(),
            PayloadJson = PendingInventoryTransferMapper.SerializePayload(request),
            Status = PendingInventoryTransferStatuses.AwaitingApproval,
            CreatedByUserId = submitter.Id,
            CreatedByName = BuildDisplayName(submitter),
            CreatedByRole = submitter.Role,
            LineCount = request.Lines?.Count ?? 0,
            TotalQuantity = request.Lines?.Sum(line => line.Quantity) ?? 0m,
            Comments = Truncate(request.Comments, 500),
            DocDate = ParseDate(request.DocDate),
            DueDate = ParseDate(request.DueDate)
        };

        context.PendingInventoryTransfers.Add(pending);
        await SaveWithDraftNumberAsync(pending, cancellationToken);

        try
        {
            var approvalRequest = await approvalService.EnsureRequestAsync(
                ApprovalDocumentContext.ForPendingTransfer(pending), submitter.Id, cancellationToken);
            pending.ApprovalRequestId = approvalRequest.Id;
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            // Without an approval request the transfer could never be actioned, so do not keep it.
            logger.LogError(exception, "Could not open an approval request for inventory transfer {PendingId}", pending.Id);
            context.PendingInventoryTransfers.Remove(pending);
            await context.SaveChangesAsync(CancellationToken.None);
            return Errors.InventoryTransfer.InvalidOperation(exception.Message);
        }

        logger.LogInformation(
            "Inventory transfer {PendingId} submitted for approval by {User}. From: {FromWarehouse}, To: {ToWarehouse}, Lines: {LineCount}",
            pending.Id, submitter.Username, pending.FromWarehouse, pending.ToWarehouse, pending.LineCount);

        try
        {
            await auditService.LogAsync(
                AuditActions.SubmitTransferForApproval, "PendingInventoryTransfer", pending.Id.ToString(),
                $"Transfer from {pending.FromWarehouse} to {pending.ToWarehouse} submitted for approval", true);
        }
        catch { }

        return BuildSubmittedResponse(pending, replayed: false);
    }

    /// <summary>
    /// Persists the held transfer, allocating its draft number. Two submissions arriving together
    /// can read the same highest number, so a unique-index violation is retried with a freshly
    /// read number rather than failing a submission that is otherwise valid.
    /// </summary>
    private async Task SaveWithDraftNumberAsync(
        PendingInventoryTransferEntity pending,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            pending.DraftNumber = await PendingTransferDraftNumbers.NextAsync(
                context, pending.CreatedAtUtc, cancellationToken);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt < maxAttempts)
            {
                logger.LogWarning(
                    "Draft number {DraftNumber} was taken while submitting a transfer; retrying (attempt {Attempt})",
                    pending.DraftNumber, attempt);
            }
        }
    }

    private static InventoryTransferCreatedResponseDto BuildSubmittedResponse(
        PendingInventoryTransferEntity pending,
        bool replayed) => new()
        {
            Message = replayed
                ? "This inventory transfer has already been submitted and is awaiting approval."
                : "Inventory transfer submitted for approval. It will post to SAP once all approval stages are complete.",
            RequiresApproval = true,
            Transfer = null,
            PendingTransfer = PendingInventoryTransferMapper.ToDto(pending)
        };

    /// <summary>
    /// Runs stock validation, returning an error only when SAP positively reports insufficient
    /// stock. Neither a SAP outage nor a SAP that is merely slow is allowed to block a submission
    /// that will not post until later.
    /// </summary>
    private async Task<ErrorOr<InventoryTransferCreatedResponseDto>?> ValidateStockBestEffortAsync(
        CreateInventoryTransferRequest request,
        CancellationToken cancellationToken)
    {
        var budget = TimeSpan.FromSeconds(
            Math.Clamp(settings.Value.TransferStockValidationBudgetSeconds, 1, 600));

        try
        {
            using var stockValidationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stockValidationCts.CancelAfter(budget);

            var stockValidationResult = await stockValidation.ValidateInventoryTransferStockAsync(
                request, stockValidationCts.Token);

            if (!stockValidationResult.IsValid)
            {
                logger.LogWarning("Stock validation failed for inventory transfer submission. {ErrorCount} items have insufficient stock",
                    stockValidationResult.Errors.Count);
                return Errors.InventoryTransfer.InsufficientStock(
                    $"Insufficient stock in source warehouse: {string.Join("; ", stockValidationResult.Errors.Select(e => e.Message))}");
            }

            if (!stockValidationResult.StockWasFullyRead)
            {
                logger.LogWarning(
                    "Holding an inventory transfer submission SAP did not answer for in warehouse(s) {Warehouses}. Stock is re-checked before posting.",
                    string.Join(", ", stockValidationResult.UnreadableWarehouses));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.InventoryTransfer.CreationFailed("Request was canceled by the client");
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Stock validation exceeded its {BudgetSeconds}s budget while submitting an inventory transfer. Holding it anyway; stock is re-checked before posting.",
                budget.TotalSeconds);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Could not validate stock while submitting an inventory transfer for approval. Stock is re-checked before posting.");
        }

        return null;
    }

    private static string BuildDisplayName(User user)
    {
        var name = string.Join(' ', new[] { user.FirstName, user.LastName }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(name) ? user.Username : name;
    }

    private static DateTime? ParseDate(string? value)
        => DateTime.TryParse(value, out var parsed) ? DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc) : null;

    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrWhiteSpace(value) ? null
            : value.Length <= maxLength ? value : value[..maxLength];

    private async Task<List<string>> ValidateTransferQuantitiesAsync(CreateInventoryTransferRequest request, CancellationToken cancellationToken)
    {
        var errors = await UomQuantityValidation.ValidateAndNormalizeLineQuantitiesAsync(
            context,
            request.Lines,
            line => line.ItemCode,
            line => line.Quantity,
            line => line.UoMCode,
            (line, uomCode) => line.UoMCode = uomCode,
            cancellationToken);

        if (request.Lines == null)
            return errors;

        for (int i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];

            if (line.BatchNumbers != null)
            {
                decimal batchTotal = 0;
                for (int j = 0; j < line.BatchNumbers.Count; j++)
                {
                    var batch = line.BatchNumbers[j];
                    if (batch.Quantity <= 0)
                        errors.Add($"Line {i + 1}, Batch {j + 1} (Batch: {batch.BatchNumber ?? "unknown"}): Quantity must be greater than zero. Current value: {batch.Quantity}");

                    var batchQuantityError = UomQuantityValidation.BuildFractionalQuantityValidationError(
                        i + 1,
                        $"{line.ItemCode ?? "unknown"} / Batch {batch.BatchNumber ?? "unknown"}",
                        batch.Quantity,
                        line.UoMCode);
                    if (!string.IsNullOrWhiteSpace(batchQuantityError))
                        errors.Add(batchQuantityError);

                    batchTotal += batch.Quantity;
                }

                if (line.BatchNumbers.Count > 0 && Math.Abs(batchTotal - line.Quantity) > 0.0001m)
                    errors.Add($"Line {i + 1} (Item: {line.ItemCode ?? "unknown"}): Batch quantities total ({batchTotal:N4}) does not match line quantity ({line.Quantity:N4})");
            }
        }

        return errors;
    }

    private async Task<List<string>?> ValidateWarehouseCodesAsync(CreateInventoryTransferRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var warehouseValidationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            warehouseValidationCts.CancelAfter(TimeSpan.FromSeconds(15));

            var warehouses = await sapClient.GetWarehousesAsync(warehouseValidationCts.Token);
            var validCodes = new HashSet<string>(warehouses.Select(w => w.WarehouseCode!), StringComparer.OrdinalIgnoreCase);

            var invalidWarehouses = new List<string>();

            if (!string.IsNullOrWhiteSpace(request.FromWarehouse) && !validCodes.Contains(request.FromWarehouse))
                invalidWarehouses.Add($"FromWarehouse '{request.FromWarehouse}' does not exist in SAP");

            if (!string.IsNullOrWhiteSpace(request.ToWarehouse) && !validCodes.Contains(request.ToWarehouse))
                invalidWarehouses.Add($"ToWarehouse '{request.ToWarehouse}' does not exist in SAP");

            if (request.Lines != null)
            {
                for (int i = 0; i < request.Lines.Count; i++)
                {
                    var line = request.Lines[i];
                    if (!string.IsNullOrWhiteSpace(line.FromWarehouseCode) && !validCodes.Contains(line.FromWarehouseCode))
                        invalidWarehouses.Add($"Line {i + 1}: FromWarehouseCode '{line.FromWarehouseCode}' does not exist in SAP");
                    if (!string.IsNullOrWhiteSpace(line.ToWarehouseCode) && !validCodes.Contains(line.ToWarehouseCode))
                        invalidWarehouses.Add($"Line {i + 1}: ToWarehouseCode '{line.ToWarehouseCode}' does not exist in SAP");
                }
            }

            return invalidWarehouses;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Warehouse validation timed out after 15s. Proceeding without warehouse validation.");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not validate warehouse codes against SAP. Proceeding without warehouse validation.");
            return null;
        }
    }
}
