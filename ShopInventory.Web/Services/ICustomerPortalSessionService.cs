using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface ICustomerPortalSessionService
{
    Task<CustomerPortalSession?> GetCurrentSessionAsync();

    /// <summary>
    /// Whether a customer's session has been proved on this circuit and not cleared since. Answers
    /// from memory, without the browser, so <see cref="WebApiCaller"/> can ask it from an HTTP handler.
    /// </summary>
    bool HasVerifiedCustomer { get; }
    bool CanAccessCardCode(CustomerPortalSession? session, string? cardCode);
    IReadOnlyList<string> ResolveAccessibleCardCodes(CustomerPortalSession? session, string? requestedCardCode);
    Task LogoutAsync();
    Task ClearSessionAsync();
}
