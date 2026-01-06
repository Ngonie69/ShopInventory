using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace ShopInventory.Web.Services;

/// <summary>
/// A no-op authentication handler for Blazor Server apps.
/// Actual authentication is handled by CustomAuthStateProvider using JWT from localStorage.
/// This handler prevents redirect loops and satisfies the authentication middleware requirements.
/// </summary>
public class BlazorServerAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public BlazorServerAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Return no result - let Blazor's AuthorizeRouteView handle authorization
        return Task.FromResult(AuthenticateResult.NoResult());
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Don't redirect - Blazor's AuthorizeRouteView will show the login page
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        // Don't redirect - let Blazor handle it
        return Task.CompletedTask;
    }
}
