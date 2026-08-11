using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Products.Queries.GetVanSaleCatalogue;

public sealed class GetVanSaleCatalogueHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetVanSaleCatalogueHandler> logger
) : IRequestHandler<GetVanSaleCatalogueQuery, ErrorOr<ProductsListResponseDto>>
{
    public async Task<ErrorOr<ProductsListResponseDto>> Handle(
        GetVanSaleCatalogueQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Product.SapDisabled;

        try
        {
            var items = await sapClient.GetVanSalesApprovedItemsAsync(cancellationToken);

            // No price. A transfer request has no customer to price against, and inventing a list
            // here would put a number on the wire that no caller could account for; the handset
            // already fills generic prices from /price/grouped, which is what it does today for any
            // warehouse product SAP prices at zero.
            var products = items.Select(item => new ProductDto
            {
                ItemCode = item.ItemCode,
                ItemName = item.ItemName,
                BarCode = item.BarCode,
                ItemType = item.ItemType,
                ManagesBatches = item.ManageBatchNumbers == "tYES",
                DefaultWarehouse = item.DefaultWarehouse,
                UoM = item.SalesUnit ?? item.InventoryUOM,
                Category = item.U_ItemGroup,
                ItemsGroupCode = item.ItemsGroupCode,

                // Empty rather than absent. The catalogue is not scoped to a warehouse, so there are
                // no batches to report, and a null here reads as a missing field to clients that
                // walk the list — which is every client, since these arrive as products.
                Batches = []
            }).ToList();

            logger.LogInformation("Retrieved {Count} van sales approved products from SAP", products.Count);

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
