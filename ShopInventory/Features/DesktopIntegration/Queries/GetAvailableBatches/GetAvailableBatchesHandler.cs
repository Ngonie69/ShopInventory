using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetAvailableBatches;

public sealed class GetAvailableBatchesHandler(
    IBatchInventoryValidationService batchValidation,
    IStockReservationService reservationService
) : IRequestHandler<GetAvailableBatchesQuery, ErrorOr<List<AvailableBatchWithReservationsDto>>>
{
    public async Task<ErrorOr<List<AvailableBatchWithReservationsDto>>> Handle(
        GetAvailableBatchesQuery query,
        CancellationToken cancellationToken)
    {
        var batches = await batchValidation.GetAvailableBatchesAsync(
            query.ItemCode, query.WarehouseCode, BatchAllocationStrategy.FEFO, cancellationToken);

        var result = new List<AvailableBatchWithReservationsDto>();

        foreach (var batch in batches)
        {
            var reservedQty = await reservationService.GetReservedBatchQuantityAsync(
                query.ItemCode, query.WarehouseCode, batch.BatchNumber ?? "", cancellationToken);

            result.Add(new AvailableBatchWithReservationsDto
            {
                BatchNumber = batch.BatchNumber ?? "",
                PhysicalQuantity = batch.AvailableQuantity,
                ReservedQuantity = reservedQty,
                AvailableQuantity = batch.AvailableQuantity - reservedQty,
                ExpiryDate = batch.ExpiryDate,
                ManufacturingDate = batch.AdmissionDate,
                Status = batch.IsRecommended ? "Recommended" : "Available"
            });
        }

        return result;
    }
}
