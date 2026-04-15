using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.Invoices.Queries.GetAvailableBatches;

public sealed class GetAvailableBatchesHandler(
    IBatchInventoryValidationService batchValidation,
    IOptions<SAPSettings> settings,
    ILogger<GetAvailableBatchesHandler> logger
) : IRequestHandler<GetAvailableBatchesQuery, ErrorOr<object>>
{
    public async Task<ErrorOr<object>> Handle(
        GetAvailableBatchesQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Invoice.SapDisabled;

        try
        {
            var batches = await batchValidation.GetAvailableBatchesAsync(
                request.ItemCode, request.WarehouseCode, request.Strategy, cancellationToken);

            if (batches.Count == 0)
                return Errors.Invoice.NoBatchesFound(request.ItemCode, request.WarehouseCode);

            return new
            {
                itemCode = request.ItemCode,
                warehouseCode = request.WarehouseCode,
                strategy = request.Strategy.ToString(),
                batchCount = batches.Count,
                totalAvailable = batches.Sum(b => b.AvailableQuantity),
                batches
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting available batches for {ItemCode} in {Warehouse}", request.ItemCode, request.WarehouseCode);
            return Errors.Invoice.CreationFailed(ex.Message);
        }
    }
}
