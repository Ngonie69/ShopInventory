using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.Merchandiser.Queries.GetGlobalProducts;

public sealed class GetGlobalProductsHandler(
    IMerchandiserService merchandiserService,
    ILogger<GetGlobalProductsHandler> logger
) : IRequestHandler<GetGlobalProductsQuery, ErrorOr<MerchandiserProductListResponse>>
{
    public async Task<ErrorOr<MerchandiserProductListResponse>> Handle(
        GetGlobalProductsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await merchandiserService.GetGlobalProductsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading global merchandiser products");
            return Errors.Merchandiser.LoadProductsFailed("Failed to load global merchandiser products.");
        }
    }
}