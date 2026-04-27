using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface ICustomerPortalSessionService
{
    Task<CustomerPortalSession?> GetCurrentSessionAsync();
    bool CanAccessCardCode(CustomerPortalSession? session, string? cardCode);
    IReadOnlyList<string> ResolveAccessibleCardCodes(CustomerPortalSession? session, string? requestedCardCode);
    Task ClearSessionAsync();
}
