using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Products.Queries.GetProductByCode;

public sealed class GetProductByCodeHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetProductByCodeHandler> logger
) : IRequestHandler<GetProductByCodeQuery, ErrorOr<ProductDto>>
{
    public async Task<ErrorOr<ProductDto>> Handle(
        GetProductByCodeQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Product.SapDisabled;

        try
        {
            var item = await sapClient.GetItemByCodeAsync(request.ItemCode, cancellationToken);

            if (item is null)
                return Errors.Product.NotFound(request.ItemCode);

            return new ProductDto
            {
                ItemCode = item.ItemCode,
                ItemName = item.ItemName,
                BarCode = item.BarCode,
                ItemType = item.ItemType,
                ManagesBatches = item.ManageBatchNumbers == "tYES",
                QuantityInStock = item.QuantityOnStock,
                QuantityAvailable = item.QuantityOnStock - item.QuantityOrderedByCustomers,
                QuantityCommitted = item.QuantityOrderedByCustomers,
                UoM = item.InventoryUOM
            };
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
