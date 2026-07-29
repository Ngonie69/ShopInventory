using Microsoft.JSInterop;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public sealed class CustomerPortalSessionService(
    IJSRuntime jsRuntime,
    ICustomerAuthService customerAuthService,
    ICustomerLinkedAccountService linkedAccountService,
    WebClientAuditContext clientAuditContext,
    ILogger<CustomerPortalSessionService> logger) : ICustomerPortalSessionService
{
    private const string AccessTokenKey = "customerToken";
    private const string RefreshTokenKey = "customerRefreshToken";
    private const string CustomerInfoKey = "customerInfo";
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public async Task<CustomerPortalSession?> GetCurrentSessionAsync()
    {
        try
        {
            var token = await GetLocalStorageItemAsync(AccessTokenKey);
            var customerInfo = string.IsNullOrWhiteSpace(token)
                ? null
                : await customerAuthService.GetCustomerInfoFromTokenAsync(token);

            if (customerInfo == null)
            {
                customerInfo = await TryRefreshSessionAsync();
                if (customerInfo == null)
                {
                    await ClearSessionAsync();
                    return null;
                }
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

    public async Task LogoutAsync()
    {
        try
        {
            var refreshToken = await GetLocalStorageItemAsync(RefreshTokenKey);
            var token = await GetLocalStorageItemAsync(AccessTokenKey);
            var customer = string.IsNullOrWhiteSpace(token)
                ? null
                : await customerAuthService.GetCustomerInfoFromTokenAsync(token);

            if (customer != null && !string.IsNullOrWhiteSpace(refreshToken))
            {
                await customerAuthService.LogoutAsync(
                    customer.CardCode,
                    refreshToken,
                    clientAuditContext.IpAddress ?? "unknown");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to revoke the customer portal refresh token during logout");
        }
        finally
        {
            await ClearSessionAsync();
        }
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
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex, "Customer portal session cannot be cleared during prerendering");
        }
    }

    private async Task<CustomerInfo?> TryRefreshSessionAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            // The layout and page body initialize concurrently. Re-read the access token
            // after taking the lock so only one caller rotates the refresh token.
            var currentToken = await GetLocalStorageItemAsync(AccessTokenKey);
            if (!string.IsNullOrWhiteSpace(currentToken))
            {
                var currentCustomer = await customerAuthService.GetCustomerInfoFromTokenAsync(currentToken);
                if (currentCustomer != null)
                {
                    return currentCustomer;
                }
            }

            var refreshToken = await GetLocalStorageItemAsync(RefreshTokenKey);
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                return null;
            }

            var response = await customerAuthService.RefreshTokenAsync(
                refreshToken,
                clientAuditContext.IpAddress ?? "unknown",
                clientAuditContext.UserAgent);

            if (!response.Success ||
                string.IsNullOrWhiteSpace(response.AccessToken) ||
                string.IsNullOrWhiteSpace(response.RefreshToken))
            {
                logger.LogInformation("Customer portal session refresh was rejected: {Message}", response.Message);
                return null;
            }

            await jsRuntime.InvokeVoidAsync("localStorage.setItem", AccessTokenKey, response.AccessToken);
            await jsRuntime.InvokeVoidAsync("localStorage.setItem", RefreshTokenKey, response.RefreshToken);

            var customer = response.Customer ??
                await customerAuthService.GetCustomerInfoFromTokenAsync(response.AccessToken);

            if (customer != null)
            {
                await jsRuntime.InvokeVoidAsync(
                    "localStorage.setItem",
                    CustomerInfoKey,
                    System.Text.Json.JsonSerializer.Serialize(customer));
            }

            return customer;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<string?> GetLocalStorageItemAsync(string key)
    {
        return await jsRuntime.InvokeAsync<string?>("localStorage.getItem", key);
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
