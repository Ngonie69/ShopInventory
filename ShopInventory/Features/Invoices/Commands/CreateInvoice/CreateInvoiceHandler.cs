using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.Invoices.Events;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.Invoices.Commands.CreateInvoice;

public sealed class CreateInvoiceHandler(
    ISAPServiceLayerClient sapClient,
    IBatchInventoryValidationService batchValidation,
    IInventoryLockService lockService,
    IInvoiceFiscalizationQueue fiscalizationQueue,
    IAuditService auditService,
    IOptions<SAPSettings> settings,
    ILogger<CreateInvoiceHandler> logger
) : IRequestHandler<CreateInvoiceCommand, ErrorOr<InvoiceCreatedResponseDto>>
{
    public async Task<ErrorOr<InvoiceCreatedResponseDto>> Handle(
        CreateInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Invoice.SapDisabled;

        var request = command.Request;
        List<string>? acquiredLockTokens = null;

        try
        {
            // Step 1b: Check for duplicate invoice by U_Van_saleorder
            if (!string.IsNullOrWhiteSpace(request.U_Van_saleorder))
            {
                var existingInvoice = await sapClient.GetInvoiceByVanSaleOrderAsync(request.U_Van_saleorder, cancellationToken);
                if (existingInvoice != null)
                {
                    logger.LogWarning(
                        "Duplicate invoice detected. U_Van_saleorder '{VanSaleOrder}' already exists as DocEntry {DocEntry}, DocNum {DocNum}",
                        request.U_Van_saleorder, existingInvoice.DocEntry, existingInvoice.DocNum);
                    return Errors.Invoice.DuplicateVanSaleOrder(request.U_Van_saleorder, existingInvoice.DocNum);
                }
            }

            // Step 2: Validate basic quantities
            var quantityErrors = ValidateQuantities(request);
            if (quantityErrors.Count > 0)
                return Errors.Invoice.ValidationFailed($"Quantity validation failed: {string.Join("; ", quantityErrors)}");

            // Step 3: Validate warehouse codes
            var warehouseErrors = ValidateWarehouseCodes(request);
            if (warehouseErrors.Count > 0)
                return Errors.Invoice.ValidationFailed($"Warehouse validation failed: {string.Join("; ", warehouseErrors)}");

            logger.LogInformation(
                "Validating batch stock for invoice with {LineCount} lines. AutoAllocate: {AutoAllocate}, Strategy: {Strategy}",
                request.Lines?.Count ?? 0, command.AutoAllocateBatches, command.AllocationStrategy);

            // Step 4: Batch-level validation with FIFO/FEFO auto-allocation
            var batchValidationResult = await batchValidation.ValidateAndAllocateBatchesAsync(
                request, command.AutoAllocateBatches, command.AllocationStrategy, cancellationToken);

            if (!batchValidationResult.IsValid)
            {
                logger.LogWarning("Batch validation failed for invoice creation. {ErrorCount} errors. Strategy: {Strategy}",
                    batchValidationResult.ValidationErrors.Count, command.AllocationStrategy);
                return Errors.Invoice.BatchValidationFailed(
                    $"Batch validation failed - would cause negative quantities: {string.Join("; ", batchValidationResult.ValidationErrors.Select(e => e.Message))}");
            }

            // Step 5: Apply auto-allocated batches
            if (batchValidationResult.BatchesAutoAllocated && batchValidationResult.AllocatedLines.Count > 0)
            {
                ApplyAllocatedBatchesToRequest(request, batchValidationResult.AllocatedLines);
                logger.LogInformation("Applied auto-allocated batches to {LineCount} lines using {Strategy} strategy",
                    batchValidationResult.AllocatedLines.Count, command.AllocationStrategy);
            }

            // Step 6: Pre-post validation with locks
            var prePostResult = await batchValidation.PrePostValidationAsync(
                request, batchValidationResult.AllocatedLines, cancellationToken);

            if (!prePostResult.IsValid)
            {
                var lockErrors = prePostResult.Errors
                    .Where(e => e.ErrorCode == BatchValidationErrorCode.LockAcquisitionFailed)
                    .ToList();

                if (lockErrors.Count > 0)
                {
                    logger.LogWarning("Lock acquisition failed for invoice creation - concurrent access detected");
                    return Errors.Invoice.LockConflict;
                }

                logger.LogWarning("Pre-post validation failed - stock may have changed. {ErrorCount} errors", prePostResult.Errors.Count);
                return Errors.Invoice.StockValidationFailed(
                    $"Pre-post validation failed - stock levels changed during processing: {string.Join("; ", prePostResult.Errors.Select(e => e.Message))}");
            }

            if (!string.IsNullOrEmpty(prePostResult.LockToken))
            {
                acquiredLockTokens = prePostResult.LockTokens.Count > 0
                    ? prePostResult.LockTokens
                    : new List<string> { prePostResult.LockToken };
            }

            // Step 7: POST to SAP
            var invoice = await sapClient.CreateInvoiceAsync(request, cancellationToken);

            logger.LogInformation(
                "Invoice created successfully in SAP. DocEntry: {DocEntry}, DocNum: {DocNum}, Customer: {CardCode}, BatchesAllocated: {BatchCount}, Strategy: {Strategy}",
                invoice.DocEntry, invoice.DocNum, invoice.CardCode,
                batchValidationResult.AllocatedLines.Sum(l => l.Batches.Count), command.AllocationStrategy);

            try { await auditService.LogAsync(AuditActions.CreateInvoice, "Invoice", invoice.DocEntry.ToString(), $"Invoice #{invoice.DocNum} created for {invoice.CardCode}", true); } catch { }

            var invoiceDto = invoice.ToDto();
            var fiscalizationResult = QueueFiscalization(invoiceDto);

            return new InvoiceCreatedResponseDto
            {
                Message = fiscalizationResult.Queued
                    ? "Invoice created successfully; fiscalization is running in the background"
                    : "Invoice created successfully (fiscalization pending)",
                Invoice = invoiceDto,
                Fiscalization = fiscalizationResult
            };
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Validation error creating invoice");
            try { await auditService.LogAsync(AuditActions.CreateInvoice, "Invoice", null, $"Validation error: {ex.Message}", false, ex.Message); } catch { }
            return Errors.Invoice.ValidationFailed(ex.Message);
        }
        catch (SapPostingPeriodException ex)
        {
            logger.LogWarning(
                "SAP rejected invoice document dates starting from DocDate {DocDate}: {Message}",
                ex.DocDate,
                ex.Message);
            try { await auditService.LogAsync(AuditActions.CreateInvoice, "Invoice", null, $"Posting period error for invoice dates; DocDate {ex.DocDate}", false, ex.Message); } catch { }
            return Errors.Invoice.PostingPeriodInvalid(ex.Message);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return Errors.Invoice.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.Invoice.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating invoice");
            return Errors.Invoice.CreationFailed(ex.Message);
        }
        finally
        {
            if (acquiredLockTokens != null && acquiredLockTokens.Count > 0)
            {
                try
                {
                    await lockService.ReleaseMultipleLocksAsync(acquiredLockTokens);
                    logger.LogDebug("Released {LockCount} inventory locks after invoice processing", acquiredLockTokens.Count);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to release inventory locks - they will expire automatically");
                }
            }
        }
    }

    private FiscalizationResult QueueFiscalization(InvoiceDto invoice)
    {
        var queued = fiscalizationQueue.TryQueue(new InvoiceFiscalizationWorkItem(
            invoice,
            new CustomerFiscalDetails { CustomerName = invoice.CardName }));

        if (queued)
        {
            logger.LogInformation(
                "Queued fiscalization for invoice {DocNum} (DocEntry: {DocEntry})",
                invoice.DocNum,
                invoice.DocEntry);

            return new FiscalizationResult
            {
                Success = true,
                Queued = true,
                Message = "Fiscalization queued",
                InvoiceNumber = invoice.DocNum.ToString()
            };
        }

        logger.LogError(
            "Failed to queue fiscalization for invoice {DocNum} (DocEntry: {DocEntry})",
            invoice.DocNum,
            invoice.DocEntry);

        return new FiscalizationResult
        {
            Success = false,
            Message = "Fiscalization could not be queued",
            ErrorCode = "FISCALIZATION_QUEUE_UNAVAILABLE",
            InvoiceNumber = invoice.DocNum.ToString()
        };
    }

    private static List<string> ValidateQuantities(CreateInvoiceRequest request)
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
            if (line.UnitPrice.HasValue && line.UnitPrice.Value <= 0)
                errors.Add($"Line {i + 1} (Item: {line.ItemCode ?? "unknown"}): Unit price must be greater than zero. Current value: {line.UnitPrice.Value}");
            else if (!line.UnitPrice.HasValue)
                errors.Add($"Line {i + 1} (Item: {line.ItemCode ?? "unknown"}): Unit price is required and must be greater than zero.");
            if (line.BatchNumbers != null)
                for (int j = 0; j < line.BatchNumbers.Count; j++)
                    if (line.BatchNumbers[j].Quantity <= 0)
                        errors.Add($"Line {i + 1}, Batch {j + 1} (Batch: {line.BatchNumbers[j].BatchNumber ?? "unknown"}): Quantity must be greater than zero.");
        }
        return errors;
    }

    private static List<string> ValidateWarehouseCodes(CreateInvoiceRequest request)
    {
        var errors = new List<string>();
        if (request.Lines == null) return errors;
        for (int i = 0; i < request.Lines.Count; i++)
            if (string.IsNullOrWhiteSpace(request.Lines[i].WarehouseCode))
                errors.Add($"Line {i + 1} (Item: {request.Lines[i].ItemCode ?? "unknown"}): Warehouse code is required for each invoice line.");
        return errors;
    }

    private void ApplyAllocatedBatchesToRequest(CreateInvoiceRequest request, List<AllocatedBatchLine> allocatedLines)
    {
        if (request.Lines == null) return;
        foreach (var allocatedLine in allocatedLines)
        {
            var lineIndex = allocatedLine.LineNumber - 1;
            if (lineIndex < 0 || lineIndex >= request.Lines.Count) continue;
            var requestLine = request.Lines[lineIndex];
            if (requestLine.BatchNumbers == null || requestLine.BatchNumbers.Count == 0)
            {
                if (allocatedLine.Batches.Count > 0)
                {
                    requestLine.BatchNumbers = allocatedLine.Batches
                        .Select(b => new BatchNumberRequest
                        {
                            BatchNumber = b.BatchNumber,
                            Quantity = b.QuantityAllocated,
                            ExpiryDate = b.ExpiryDate
                        }).ToList();
                    logger.LogDebug("Applied {BatchCount} batches to line {LineNumber} for item {ItemCode}",
                        allocatedLine.Batches.Count, allocatedLine.LineNumber, allocatedLine.ItemCode);
                }
            }
        }
    }
}
