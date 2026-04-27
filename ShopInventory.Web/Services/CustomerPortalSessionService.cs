using Microsoft.JSInterop;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public sealed class CustomerPortalSessionService(
    IJSRuntime jsRuntime,
    ICustomerAuthService customerAuthService,
    ICustomerLinkedAccountService linkedAccountService,
    ILogger<CustomerPortalSessionService> logger) : ICustomerPortalSessionService
{
    private const string AccessTokenKey = "customerToken";
    private const string RefreshTokenKey = "customerRefreshToken";
    private const string CustomerInfoKey = "customerInfo";

    public async Task<CustomerPortalSession?> GetCurrentSessionAsync()
    {
        try
        {
            var token = await jsRuntime.InvokeAsync<string?>("localStorage.getItem", AccessTokenKey);
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            var customerInfo = await customerAuthService.GetCustomerInfoFromTokenAsync(token);
            if (customerInfo == null)
            {
                await ClearSessionAsync();
                return null;
            }

            var linkedAccounts = customerInfo.AccountStructure == "Multi"
                ? await GetLinkedAccountsAsync(customerInfo)
                : new List<LinkedAccountInfo>();

            var accessibleCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                customerInfo.CardCode
            };

            foreach (var cardCode in await linkedAccountService.GetAllCardCodesAsync(customerInfo.CardCode))
            {
                if (!string.IsNullOrWhiteSpace(cardCode))
                {
                    accessibleCodes.Add(cardCode);
                }
            }

            foreach (var account in linkedAccounts)
            {
                if (!string.IsNullOrWhiteSpace(account.CardCode))
                {
                    accessibleCodes.Add(account.CardCode);
                }
            }

            customerInfo.LinkedAccounts = linkedAccounts;

            return new CustomerPortalSession(customerInfo, linkedAccounts, accessibleCodes.ToList());
        }
        catch (JSDisconnectedException)
        {
            return null;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex, "Customer portal session is not available during prerendering");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to resolve customer portal session");
            return null;
        }
    }

    public bool CanAccessCardCode(CustomerPortalSession? session, string? cardCode)
    {
        return session != null &&
            (string.IsNullOrWhiteSpace(cardCode) ||
             session.AccessibleCardCodes.Contains(cardCode, StringComparer.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> ResolveAccessibleCardCodes(CustomerPortalSession? session, string? requestedCardCode)
    {
        if (session == null)
        {
            return Array.Empty<string>();
        }

        if (string.IsNullOrWhiteSpace(requestedCardCode))
        {
            return session.AccessibleCardCodes;
        }

        return CanAccessCardCode(session, requestedCardCode)
            ? new[] { requestedCardCode }
            : Array.Empty<string>();
    }

    public async Task ClearSessionAsync()
    {
        try
        {
            await jsRuntime.InvokeVoidAsync("localStorage.removeItem", AccessTokenKey);
            await jsRuntime.InvokeVoidAsync("localStorage.removeItem", RefreshTokenKey);
            await jsRuntime.InvokeVoidAsync("localStorage.removeItem", CustomerInfoKey);
        }
        catch (JSDisconnectedException)
        {
        }
    }

    private async Task<List<LinkedAccountInfo>> GetLinkedAccountsAsync(CustomerInfo customerInfo)
    {
        if (customerInfo.LinkedAccounts.Count > 0)
        {
            return customerInfo.LinkedAccounts;
        }

        return await linkedAccountService.GetLinkedAccountsAsync(customerInfo.CardCode);
    }
}
