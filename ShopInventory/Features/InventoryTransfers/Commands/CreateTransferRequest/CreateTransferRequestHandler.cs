using ErrorOr;
using MediatR;
using ShopInventory.Common.Validation;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.InventoryTransfers.Commands.CreateTransferRequest;

public sealed class CreateTransferRequestHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IAuditService auditService,
    IOptions<SAPSettings> settings,
    ILogger<CreateTransferRequestHandler> logger
) : IRequestHandler<CreateTransferRequestCommand, ErrorOr<TransferRequestCreatedResponseDto>>
{
    public async Task<ErrorOr<TransferRequestCreatedResponseDto>> Handle(
        CreateTransferRequestCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.InventoryTransfer.SapDisabled;

        var request = command.Request;

        // Validate positive quantities
        var quantityErrors = await ValidateTransferRequestQuantitiesAsync(request, cancellationToken);
        if (quantityErrors.Count > 0)
        {
            logger.LogWarning("Transfer request quantity validation failed: {Errors}", string.Join(", ", quantityErrors));
            return Errors.InventoryTransfer.ValidationFailed(
            $"Quantity validation failed: {string.Join("; ", quantityErrors)}");
        }

        try
        {
            // Validate warehouse codes exist in SAP
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

                if (invalidWarehouses.Count > 0)
                {
                    logger.LogWarning("Invalid warehouse codes in transfer request: {Errors}", string.Join(", ", invalidWarehouses));
                    return Errors.InventoryTransfer.InvalidWarehouse(string.Join("; ", invalidWarehouses));
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Warehouse validation timed out after 15s. Proceeding without warehouse validation.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not validate warehouse codes against SAP. Proceeding without warehouse validation.");
            }

            logger.LogInformation("Creating transfer request with {LineCount} lines from {FromWarehouse} to {ToWarehouse}",
                request.Lines?.Count ?? 0, request.FromWarehouse, request.ToWarehouse);

            var transferRequest = await sapClient.CreateInventoryTransferRequestAsync(request, cancellationToken);

            logger.LogInformation("Transfer request created successfully. DocEntry: {DocEntry}, DocNum: {DocNum}, From: {FromWarehouse}, To: {ToWarehouse}",
                transferRequest.DocEntry, transferRequest.DocNum, request.FromWarehouse, request.ToWarehouse);

            try { await auditService.LogAsync(AuditActions.CreateTransferRequest, "TransferRequest", transferRequest.DocEntry.ToString(), $"Transfer request #{transferRequest.DocNum} from {request.FromWarehouse} to {request.ToWarehouse}", true); } catch { }

            return new TransferRequestCreatedResponseDto
            {
                Message = "Transfer request created successfully",
                TransferRequest = transferRequest.ToDto()
            };
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Validation error creating transfer request");
            return Errors.InventoryTransfer.ValidationFailed(ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.InventoryTransfer.CreationFailed("Request was canceled by the client");
        }
        catch (OperationCanceledException ex)
        {
            logger.LogError(ex, "Timeout or connection abort creating transfer request");
            return Errors.InventoryTransfer.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.InventoryTransfer.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating transfer request");
            return Errors.InventoryTransfer.CreationFailed(ex.Message);
        }
    }

    private Task<List<string>> ValidateTransferRequestQuantitiesAsync(CreateTransferRequestDto request, CancellationToken cancellationToken)
        => UomQuantityValidation.ValidateAndNormalizeLineQuantitiesAsync(
            context,
            request.Lines,
            line => line.ItemCode,
            line => line.Quantity,
            line => line.UoMCode,
            (line, uomCode) => line.UoMCode = uomCode,
            cancellationToken);
}
