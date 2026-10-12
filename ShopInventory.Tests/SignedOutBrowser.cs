using Blazored.LocalStorage;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A browser nobody has signed in on, for tests of a Web API service that are not about who is
/// asking: there is no token to attach and none to renew, so the service's calls go out exactly as
/// the test's handler expects them.
/// </summary>
internal sealed class SignedOutBrowser
{
    public SignedOutBrowser(HttpClient httpClient)
    {
        LocalStorage = ExpiringSession.BrowserStorage([]);
        AuthStateProvider = new CustomAuthStateProvider(
            LocalStorage,
            httpClient,
            NullLogger<CustomAuthStateProvider>.Instance,
            new WebClientAuditContext());
    }

    public ILocalStorageService LocalStorage { get; }

    public CustomAuthStateProvider AuthStateProvider { get; }

    /// <summary>The desktop integration service over <paramref name="httpClient"/>, signed out.</summary>
    public static DesktopIntegrationService DesktopIntegration(HttpClient httpClient)
    {
        var browser = new SignedOutBrowser(httpClient);
        return new DesktopIntegrationService(
            httpClient,
            NullLogger<DesktopIntegrationService>.Instance,
            browser.LocalStorage,
            browser.AuthStateProvider);
    }
}
