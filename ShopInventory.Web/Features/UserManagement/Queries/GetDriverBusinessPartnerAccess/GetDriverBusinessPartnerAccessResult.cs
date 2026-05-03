using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.UserManagement.Queries.GetDriverBusinessPartnerAccess;

public sealed class GetDriverBusinessPartnerAccessResult
{
    public List<BusinessPartnerDto> Customers { get; init; } = new();
    public List<string> AssignedCustomerCodes { get; init; } = new();
}