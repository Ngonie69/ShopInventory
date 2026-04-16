using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Queries.ValidateInvoice;

public sealed class ValidateInvoiceHandler(
    IBatchInventoryValidationService batchValidation,
    IOptions<SAPSettings> sapSettings,
    ILogger<ValidateInvoiceHandler> logger
) : IRequestHandler<ValidateInvoiceQuery, ErrorOr<ValidateInvoiceResult>>
{
    public async Task<ErrorOr<ValidateInvoiceResult>> Handle(
        ValidateInvoiceQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.DesktopIntegration.SapDisabled;

        try
        {
            var request = query.Request;

            // Convert to CreateInvoiceRequest for batch validation
            var invoiceRequest = new CreateInvoiceRequest
            {
                CardCode = request.CardCode,
                DocDate = request.DocDate,
                DocDueDate = request.DocDueDate,
                NumAtCard = request.NumAtCard,
                Comments = request.Comments,
                DocCurrency = request.DocCurrency,
                SalesPersonCode = request.SalesPersonCode,
                Lines = request.Lines.Select(l => new CreateInvoiceLineRequest
                {
                    ItemCode = l.ItemCode,
                    Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice ?? 0,
                    WarehouseCode = l.WarehouseCode,
                    TaxCode = l.TaxCode,
                    DiscountPercent = l.DiscountPercent ?? 0,
                    UoMCode = l.UoMCode,
                    BatchNumbers = l.BatchNumbers?.Select(b => new BatchNumberRequest
                    {
                        BatchNumber = b.BatchNumber,
                        Quantity = b.Quantity
                    }).ToList()
                }).ToList()
            };

            var result = await batchValidation.ValidateAndAllocateBatchesAsync(
                invoiceRequest, query.AutoAllocateBatches, query.AllocationStrategy, cancellationToken);

            if (result.IsValid)
            {
                return new ValidateInvoiceResult(
                    IsValid: true,
                    Message: "Invoice validation successful",
                    Strategy: query.AllocationStrategy.ToString(),
                    LinesValidated: result.TotalLinesValidated,
                    BatchesAllocated: result.AllocatedLines.Sum(l => l.Batches.Count),
                    AllocatedLines: result.AllocatedLines,
                    Warnings: result.Warnings
                );
            }

            return Errors.DesktopIntegration.ValidationFailed(
                string.Join("; ", result.ValidationErrors.Select(e => e.Message ?? e.ErrorCode.ToString())));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error validating invoice");
            return Errors.DesktopIntegration.ValidationFailed(ex.Message);
        }
    }
}
