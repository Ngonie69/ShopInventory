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

    /// <summary>
    /// How long a resolved session is reused before it is proved again.
    /// </summary>
    /// <remarks>
    /// Short on purpose. This memo is what stands between a deactivated or locked-out customer and
    /// the rest of their visit, so it is sized to cover one page load and an immediate navigation,
    /// not a browsing session.
    /// </remarks>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private CustomerPortalSession? _cachedSession;
    private DateTimeOffset _cachedSessionExpiresAt;

    /// <summary>
    /// Resolves the signed-in customer, reusing a recent resolution rather than proving the session
    /// from scratch for every caller.
    /// </summary>
    /// <remarks>
    /// Resolving a session is not cheap: it reads local storage over JS interop, loads the portal
    /// user from the web database, and then reads the business partner from SAP through the API.
    /// Every portal page asks for it, and the layout asks for it too — the layout and the page body
    /// initialise concurrently — so a single page view paid for that whole chain twice, and paid it
    /// again on every navigation. This service is scoped, which in Blazor Server means one instance
    /// per circuit, so memoising here collapses the layout/page pair into one resolution and makes
    /// moving between portal pages free for the length of <see cref="SessionLifetime"/>.
    ///
    /// The gate matters as much as the memo: without it the concurrent layout and page callers both
    /// miss the empty cache and both do the full resolution, which is the exact case being removed.
    ///
    /// Only a resolved session is cached. Caching "no session" would mean a customer who has just
    /// signed in keeps being told they are signed out for the rest of the window, and the null path
    /// costs nothing anyway — no token means no database read and no SAP call.
    /// </remarks>
    public async Task<CustomerPortalSession?> GetCurrentSessionAsync()
    {
        if (TryGetCachedSession(out var cached))
        {
            return cached;
        }

        await _sessionGate.WaitAsync();
        try
        {
            if (TryGetCachedSession(out cached))
            {
                return cached;
            }

            var session = await ResolveSessionAsync();
            if (session is not null)
            {
                _cachedSession = session;
                _cachedSessionExpiresAt = DateTimeOffset.UtcNow.Add(SessionLifetime);
            }

            return session;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private bool TryGetCachedSession(out CustomerPortalSession? session)
    {
        session = _cachedSession;
        return session is not null && DateTimeOffset.UtcNow < _cachedSessionExpiresAt;
    }

    private void InvalidateCachedSession()
    {
        _cachedSession = null;
        _cachedSessionExpiresAt = DateTimeOffset.MinValue;
    }

    private async Task<CustomerPortalSession?> ResolveSessionAsync()
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
        // Before the tokens go, so a caller racing this cannot re-cache the session being cleared.
        InvalidateCachedSession();

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
