using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.UserManagement.Queries.GetManagedMerchandiserAccounts;

public sealed class GetManagedMerchandiserAccountsResult
{
    public List<ManagedMerchandiserAccountModel> Merchandisers { get; init; } = new();
    public List<BusinessPartnerDto> Customers { get; init; } = new();
    public List<WarehouseDto> Warehouses { get; init; } = new();
}