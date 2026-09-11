using System.Security.Claims;
using ShopInventory.Models;

namespace ShopInventory.Authentication;

/// <summary>
/// Tells an integration acting as itself apart from a person whose request happens to carry an API key.
/// </summary>
/// <remarks>
/// A request can carry an <c>X-API-Key</c> and a user's bearer token together. ShopInventory.Web sends
/// both on every call, and the KefShop till does too. The "ApiAccess" policy authenticates both
/// schemes, so the principal ends up with two identities: the key's, holding the roles from its
/// configuration (Admin, for the Web's key), and the user's own.
///
/// Asked of the whole principal, <c>IsInRole(Admin)</c> and the ApiKey authentication-method claim
/// are then true for every Web user. A bypass written against them let every Web request skip its
/// permission check: a cashier passed <c>creditnotes.add_approved</c>. So the question is asked per
/// identity. A service call is one where a privileged key authenticated and nothing else did. Once a
/// user token is also present, the user is the one acting and the key is only the channel.
/// </remarks>
public static class ApiKeyServiceCaller
{
    /// <summary>
    /// True when the only authenticated identities are API keys, and one of them holds Admin or
    /// ApiUser. False as soon as any other authenticated identity — a user's token — is present.
    /// </summary>
    public static bool IsServiceCall(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return false;
        }

        var authenticated = principal.Identities.Where(identity => identity.IsAuthenticated).ToList();
        if (authenticated.Count == 0 || !authenticated.All(IsApiKeyIdentity))
        {
            return false;
        }

        return authenticated.Any(identity =>
            identity.HasClaim(identity.RoleClaimType, ApplicationRoles.Admin) ||
            identity.HasClaim(identity.RoleClaimType, ApplicationRoles.ApiUser));
    }

    /// <summary>
    /// Whether this identity was issued by <see cref="ApiKeyAuthenticationHandler"/>.
    /// </summary>
    public static bool IsApiKeyIdentity(ClaimsIdentity identity)
    {
        return string.Equals(identity.FindFirst(ClaimTypes.AuthenticationMethod)?.Value, AuthenticationSchemes.ApiKey, StringComparison.OrdinalIgnoreCase)
            || identity.HasClaim(claim => string.Equals(claim.Type, "api_key_name", StringComparison.Ordinal));
    }
}
