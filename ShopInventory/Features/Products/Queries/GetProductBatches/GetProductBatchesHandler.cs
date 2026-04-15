using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Products.Queries.GetProductBatches;

public sealed class GetProductBatchesHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetProductBatchesHandler> logger
) : IRequestHandler<GetProductBatchesQuery, ErrorOr<ProductBatchesResponseDto>>
{
    public async Task<ErrorOr<ProductBatchesResponseDto>> Handle(
        GetProductBatchesQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Product.SapDisabled;

        try
        {
            var item = await sapClient.GetItemByCodeAsync(request.ItemCode, cancellationToken);

            if (item is null)
                return Errors.Product.NotFound(request.ItemCode);

            var batches = await sapClient.GetBatchNumbersForItemInWarehouseAsync(
                request.ItemCode, request.WarehouseCode, cancellationToken);

            var batchDtos = batches.Select(b => new BatchDto
            {
                BatchNumber = b.BatchNum,
                Quantity = b.Quantity,
                Status = b.Status,
                ExpiryDate = b.ExpiryDate,
                ManufacturingDate = b.ManufacturingDate,
                AdmissionDate = b.AdmissionDate,
                Location = b.Location,
                Notes = b.Notes
            }).ToList();

            var response = new ProductBatchesResponseDto
            {
                WarehouseCode = request.WarehouseCode,
                ItemCode = item.ItemCode,
                ItemName = item.ItemName,
                TotalQuantity = batches.Sum(b => b.Quantity),
                BatchCount = batches.Count,
                Batches = batchDtos
            };

            logger.LogInformation("Retrieved {BatchCount} batches for item {ItemCode} in warehouse {Warehouse}",
                response.BatchCount, request.ItemCode, request.WarehouseCode);

            return response;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return Errors.Product.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.Product.SapConnectionError(ex.Message);
        }
    }
}
