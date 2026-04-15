using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Products.Queries.GetAllProducts;

public sealed class GetAllProductsHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetAllProductsHandler> logger
) : IRequestHandler<GetAllProductsQuery, ErrorOr<ProductsListResponseDto>>
{
    public async Task<ErrorOr<ProductsListResponseDto>> Handle(
        GetAllProductsQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Product.SapDisabled;

        try
        {
            var items = await sapClient.GetAllItemsAsync(cancellationToken);

            var products = items.Select(item => new ProductDto
            {
                ItemCode = item.ItemCode,
                ItemName = item.ItemName,
                BarCode = item.BarCode,
                ItemType = item.ItemType,
                ManagesBatches = item.ManageBatchNumbers == "tYES",
                DefaultWarehouse = item.DefaultWarehouse
            }).ToList();

            logger.LogInformation("Retrieved {Count} products from SAP", products.Count);

            return new ProductsListResponseDto
            {
                Count = products.Count,
                Products = products
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
