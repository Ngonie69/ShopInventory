using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.InventoryTransfers.Commands.CreateInventoryTransfer;

public sealed class CreateInventoryTransferHandler(
    ISAPServiceLayerClient sapClient,
    IStockValidationService stockValidation,
    IAuditService auditService,
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
        var quantityErrors = ValidateTransferQuantities(request);
        if (quantityErrors.Count > 0)
        {
            logger.LogWarning("Transfer quantity validation failed: {Errors}", string.Join(", ", quantityErrors));
            return Errors.InventoryTransfer.ValidationFailed(
                $"Quantity validation failed - negative or zero quantities are not allowed: {string.Join("; ", quantityErrors)}");
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

            logger.LogInformation("Inventory transfer created successfully. DocEntry: {DocEntry}, DocNum: {DocNum}, From: {FromWarehouse}, To: {ToWarehouse}",
                transfer.DocEntry, transfer.DocNum, request.FromWarehouse, request.ToWarehouse);

            try { await auditService.LogAsync(AuditActions.CreateTransfer, "InventoryTransfer", transfer.DocEntry.ToString(), $"Transfer #{transfer.DocNum} from {request.FromWarehouse} to {request.ToWarehouse}", true); } catch { }

            return new InventoryTransferCreatedResponseDto
            {
                Message = "Inventory transfer created successfully",
                Transfer = transfer.ToDto()
            };
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

    private static List<string> ValidateTransferQuantities(CreateInventoryTransferRequest request)
    {
        var errors = new List<string>();

        if (request.Lines == null || request.Lines.Count == 0)
        {
            errors.Add("At least one line item is required");
            return errors;
        }

        for (int i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];

            if (line.Quantity <= 0)
                errors.Add($"Line {i + 1} (Item: {line.ItemCode ?? "unknown"}): Quantity must be greater than zero. Current value: {line.Quantity}");

            if (line.BatchNumbers != null)
            {
                decimal batchTotal = 0;
                for (int j = 0; j < line.BatchNumbers.Count; j++)
                {
                    var batch = line.BatchNumbers[j];
                    if (batch.Quantity <= 0)
                        errors.Add($"Line {i + 1}, Batch {j + 1} (Batch: {batch.BatchNumber ?? "unknown"}): Quantity must be greater than zero. Current value: {batch.Quantity}");
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
