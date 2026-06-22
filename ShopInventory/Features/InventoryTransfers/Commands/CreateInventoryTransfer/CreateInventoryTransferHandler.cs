using ErrorOr;
using MediatR;
using ShopInventory.Common.Validation;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.InventoryTransfers.Commands.CreateInventoryTransfer;

public sealed class CreateInventoryTransferHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IInventoryTransferQueueService transferQueueService,
    IStockValidationService stockValidation,
    IAuditService auditService,
    INotificationService notificationService,
    SapCircuitBreakerState sapCircuitBreakerState,
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

        // Validate positive quantities
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
            return await HandleCoreAsync(request, cancellationToken);

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
                    logger.LogWarning("Replaying inventory transfer creation for idempotency key {Key}", idempotencyKey);
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

            var result = await HandleCoreAsync(request, cancellationToken);

            // Complete on any successful terminal result (posted OR queued) so a retry replays it
            // instead of posting/queuing a duplicate.
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
                    await idempotencyRequestStore.ReleaseAsync(idempotencyRequestId.Value, cancellationToken);
                }
                catch (Exception releaseException)
                {
                    logger.LogWarning(releaseException, "Failed to release inventory transfer idempotency request {RequestId}", idempotencyRequestId.Value);
                }
            }
        }
    }

    private async Task<ErrorOr<InventoryTransferCreatedResponseDto>> HandleCoreAsync(
        CreateInventoryTransferRequest request,
        CancellationToken cancellationToken)
    {
        if (sapCircuitBreakerState.IsOpen && CanQueueFallback(request))
        {
            return await QueueTransferFallbackAsync(request, cancellationToken);
        }

        try
        {
            // Run warehouse validation and stock validation in parallel
            logger.LogInformation("Validating stock availability for transfer with {LineCount} lines from {FromWarehouse} to {ToWarehouse}",
                request.Lines?.Count ?? 0, request.FromWarehouse, request.ToWarehouse);

            using var warehouseValidationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            warehouseValidationCts.CancelAfter(TimeSpan.FromSeconds(15));
            var warehouseTask = ValidateWarehouseCodesAsync(request, warehouseValidationCts.Token);
            var stockTask = stockValidation.ValidateInventoryTransferStockAsync(request, cancellationToken);

            await Task.WhenAll(warehouseTask, stockTask);

            // Check warehouse validation result
            var warehouseErrors = await warehouseTask;
            if (warehouseErrors != null && warehouseErrors.Count > 0)
            {
                logger.LogWarning("Invalid warehouse codes in transfer request: {Errors}", string.Join(", ", warehouseErrors));
                return Errors.InventoryTransfer.InvalidWarehouse(string.Join("; ", warehouseErrors));
            }

            var stockValidationResult = await stockTask;
            if (!stockValidationResult.IsValid)
            {
                logger.LogWarning("Stock validation failed for inventory transfer. {ErrorCount} items have insufficient stock",
                    stockValidationResult.Errors.Count);
                return Errors.InventoryTransfer.InsufficientStock(
                    $"Insufficient stock in source warehouse: {string.Join("; ", stockValidationResult.Errors.Select(e => e.Message))}");
            }

            var transfer = await sapClient.CreateInventoryTransferAsync(request, stockValidationResult.PreFetchedData, cancellationToken);
            var transferDto = transfer.ToDto();

            logger.LogInformation("Inventory transfer created successfully. DocEntry: {DocEntry}, DocNum: {DocNum}, From: {FromWarehouse}, To: {ToWarehouse}",
                transfer.DocEntry, transfer.DocNum, request.FromWarehouse, request.ToWarehouse);

            try { await auditService.LogAsync(AuditActions.CreateTransfer, "InventoryTransfer", transfer.DocEntry.ToString(), $"Transfer #{transfer.DocNum} from {request.FromWarehouse} to {request.ToWarehouse}", true); } catch { }

            try
            {
                var fromWarehouse = transferDto.FromWarehouse ?? request.FromWarehouse ?? "unknown";
                var toWarehouse = transferDto.ToWarehouse ?? request.ToWarehouse ?? "unknown";

                await notificationService.CreateNotificationAsync(
                    ModuleNotificationFactory.CreateBroadcastNotification(
                        $"Inventory Transfer Created: #{transfer.DocNum}",
                        $"Inventory transfer #{transfer.DocNum} from {fromWarehouse} to {toWarehouse} was created successfully.",
                        "Success",
                        "InventoryTransfer",
                        "InventoryTransfer",
                        transfer.DocEntry.ToString(),
                        "/inventory-transfers",
                        new Dictionary<string, string>
                        {
                            ["docEntry"] = transfer.DocEntry.ToString(),
                            ["docNum"] = transfer.DocNum.ToString(),
                            ["fromWarehouse"] = fromWarehouse,
                            ["toWarehouse"] = toWarehouse
                        }),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish inventory transfer notification for DocEntry {DocEntry}", transfer.DocEntry);
            }

            return new InventoryTransferCreatedResponseDto
            {
                Message = "Inventory transfer created successfully",
                Transfer = transferDto
            };
        }
        catch (Exception ex) when (SapFailureClassifier.IsTransient(ex, cancellationToken) && CanQueueFallback(request))
        {
            logger.LogWarning(ex, "SAP is unavailable while creating inventory transfer. Falling back to queue.");
            return await QueueTransferFallbackAsync(request, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Validation error creating inventory transfer");
            return Errors.InventoryTransfer.ValidationFailed(ex.Message);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("negative"))
        {
            logger.LogError(ex, "CRITICAL: Attempted transfer would result in negative stock");
            return Errors.InventoryTransfer.NegativeStock(ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.InventoryTransfer.CreationFailed("Request was canceled by the client");
        }
        catch (OperationCanceledException ex)
        {
            logger.LogError(ex, "Timeout or connection abort creating inventory transfer");
            return Errors.InventoryTransfer.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.InventoryTransfer.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating inventory transfer");
            return Errors.InventoryTransfer.CreationFailed(ex.Message);
        }
    }

    private static bool CanQueueFallback(CreateInventoryTransferRequest request)
    {
        return request.Lines?.All(line => line.SerialNumbers == null || line.SerialNumbers.Count == 0) == true;
    }

    private async Task<ErrorOr<InventoryTransferCreatedResponseDto>> QueueTransferFallbackAsync(
        CreateInventoryTransferRequest request,
        CancellationToken cancellationToken)
    {
        var queueableRequest = new CreateDesktopTransferRequest
        {
            ExternalReference = $"API-TRF-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString()[..8]}",
            SourceSystem = "API",
            FromWarehouse = request.FromWarehouse ?? string.Empty,
            ToWarehouse = request.ToWarehouse ?? string.Empty,
            DocDate = request.DocDate,
            DueDate = request.DueDate,
            Comments = request.Comments,
            IsTransferRequest = false,
            Lines = request.Lines?.Select((line, index) => new CreateDesktopTransferLineRequest
            {
                LineNum = index,
                ItemCode = line.ItemCode ?? string.Empty,
                Quantity = line.Quantity,
                UoMCode = line.UoMCode,
                FromWarehouseCode = line.FromWarehouseCode,
                WarehouseCode = line.ToWarehouseCode,
                AutoAllocateBatches = line.BatchNumbers == null || line.BatchNumbers.Count == 0,
                BatchNumbers = line.BatchNumbers?.Select(batch => new TransferBatchRequest
                {
                    BatchNumber = batch.BatchNumber,
                    Quantity = batch.Quantity
                }).ToList()
            }).ToList() ?? new List<CreateDesktopTransferLineRequest>()
        };

        var queueResult = await transferQueueService.EnqueueTransferAsync(
            queueableRequest,
            null,
            null,
            cancellationToken);

        if (!queueResult.Success)
        {
            return Errors.InventoryTransfer.CreationFailed(
                queueResult.ErrorMessage ?? "SAP is unavailable and transfer queue fallback failed");
        }

        return new InventoryTransferCreatedResponseDto
        {
            Message = "SAP is currently unavailable. The inventory transfer has been queued for deferred processing.",
            WasQueued = true,
            QueueId = queueResult.QueueId,
            QueueStatus = queueResult.Status,
            QueueExternalReference = queueResult.ExternalReference,
            EstimatedProcessingSeconds = queueResult.EstimatedProcessingTime.HasValue
                ? (int)Math.Ceiling(queueResult.EstimatedProcessingTime.Value.TotalSeconds)
                : null
        };
    }

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
            var warehouses = await sapClient.GetWarehousesAsync(cancellationToken);
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
