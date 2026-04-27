using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.UserManagement.Queries.GetManagedMerchandiserAccounts;

public sealed class GetManagedMerchandiserAccountsHandler(
    IUserManagementService userManagementService,
    IMasterDataCacheService masterDataCacheService,
    ILogger<GetManagedMerchandiserAccountsHandler> logger
) : IRequestHandler<GetManagedMerchandiserAccountsQuery, ErrorOr<GetManagedMerchandiserAccountsResult>>
{
    public async Task<ErrorOr<GetManagedMerchandiserAccountsResult>> Handle(
        GetManagedMerchandiserAccountsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var merchandisersTask = userManagementService.GetManagedMerchandiserAccountsAsync(cancellationToken);
            var customersTask = masterDataCacheService.GetBusinessPartnersAsync(includeInactive: true);
            var warehousesTask = masterDataCacheService.GetWarehousesAsync();

            await Task.WhenAll(merchandisersTask, customersTask, warehousesTask);

            return new GetManagedMerchandiserAccountsResult
            {
                Merchandisers = await merchandisersTask,
                Customers = (await customersTask).Where(IsCustomerBusinessPartner).ToList(),
                Warehouses = await warehousesTask
            };
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Failed to load managed merchandiser accounts");
            return Errors.UserManagement.GetManagedMerchandiserAccountsFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error loading managed merchandiser accounts");
            return Errors.UserManagement.GetManagedMerchandiserAccountsFailed("Failed to load merchandiser accounts.");
        }
    }

    private static bool IsCustomerBusinessPartner(BusinessPartnerDto businessPartner)
    {
        return string.Equals(businessPartner.CardType, "cCustomer", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(businessPartner.CardType, "C", StringComparison.OrdinalIgnoreCase);
    }
}