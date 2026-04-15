using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.Invoices.Queries.ValidateInvoice;

public sealed class ValidateInvoiceHandler(
    IBatchInventoryValidationService batchValidation,
    IOptions<SAPSettings> settings,
    ILogger<ValidateInvoiceHandler> logger
) : IRequestHandler<ValidateInvoiceQuery, ErrorOr<object>>
{
    public async Task<ErrorOr<object>> Handle(
        ValidateInvoiceQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Invoice.SapDisabled;

        try
        {
            // Validate warehouse codes
            if (request.Request.Lines != null)
            {
                var warehouseErrors = new List<string>();
                for (int i = 0; i < request.Request.Lines.Count; i++)
                    if (string.IsNullOrWhiteSpace(request.Request.Lines[i].WarehouseCode))
                        warehouseErrors.Add($"Line {i + 1}: Warehouse code is required");

                if (warehouseErrors.Count > 0)
                    return Errors.Invoice.ValidationFailed($"Warehouse validation failed: {string.Join("; ", warehouseErrors)}");
            }

            var result = await batchValidation.ValidateAndAllocateBatchesAsync(
                request.Request, request.AutoAllocateBatches, request.AllocationStrategy, cancellationToken);

            if (result.IsValid)
            {
                return new
                {
                    isValid = true,
                    message = "Invoice validation successful",
                    strategy = request.AllocationStrategy.ToString(),
                    linesValidated = result.TotalLinesValidated,
                    batchesAllocated = result.AllocatedLines.Sum(l => l.Batches.Count),
                    allocatedLines = result.AllocatedLines,
                    warnings = result.Warnings
                };
            }

            return Errors.Invoice.BatchValidationFailed(
                $"Invoice validation failed: {string.Join("; ", result.ValidationErrors.Select(e => e.Message))}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error validating invoice");
            return Errors.Invoice.CreationFailed(ex.Message);
        }
    }
}
