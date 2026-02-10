using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace ShopInventory.Web.Services;

/// <summary>
/// A pass-through authentication handler for Blazor Server apps.
/// Actual authentication is handled by CustomAuthStateProvider using JWT from localStorage.
/// This handler returns an anonymous authenticated principal to allow the HTTP request through,
/// then Blazor's AuthorizeRouteView handles real authorization after WebSocket connection.
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
        // Return a successful authenticated principal to let the request through.
        // Blazor's AuthorizeRouteView will handle real authorization using JWT from localStorage.
        // The authentication type must be set for IsAuthenticated to return true.
        var identity = new ClaimsIdentity("BlazorServer"); // Named identity = IsAuthenticated returns true
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
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
