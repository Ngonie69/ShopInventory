using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;

namespace ShopInventory.Authentication;

/// <summary>
/// Judges a role gate by the signed-in user whenever a request carries an API key and a user's token
/// together. The key's roles count only when the key is calling on its own.
/// </summary>
/// <remarks>
/// ShopInventory.Web sends its <c>X-API-Key</c> and the signed-in user's JWT on every call, and the
/// "ApiAccess" and "ApiAccessWithOperator" policies authenticate both schemes, so the principal holds
/// the key's identity (Admin and ApiUser) beside the user's. ASP.NET Core's own handler for
/// <see cref="RolesAuthorizationRequirement"/> asks the whole principal, and the key's Admin answers
/// for everyone: a merchandiser passed <c>[Authorize(Roles = "Admin,Cashier")]</c> on CreateInvoice.
///
/// Every <c>[Authorize(Roles = ...)]</c> and every <c>policy.RequireRole(...)</c> becomes this one
/// requirement type, so handling the type covers all of them, including gates written later.
///
/// The framework's handler still runs and still succeeds on the merged principal, so this one does not
/// succeed anything — it fails the requirement, which outranks any success. Two cases are left alone
/// because one identity already decides them: a key calling with no user (the customer portal, the
/// transfer event listener, the Web's background jobs), and a user token with no key (handsets, tills,
/// van sales customers).
///
/// The roles are read from the user's token rather than the Users table: that is what a token-only
/// caller has always been judged by, so both kinds of caller answer the same way. Scoping inside an
/// action that must survive a role change reads the account instead (ICallerAccountReader).
/// </remarks>
public sealed class ActingUserRoleAuthorizationHandler : AuthorizationHandler<RolesAuthorizationRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        RolesAuthorizationRequirement requirement)
    {
        var authenticated = context.User.Identities.Where(identity => identity.IsAuthenticated).ToList();
        var userIdentities = authenticated.Where(identity => !ApiKeyServiceCaller.IsApiKeyIdentity(identity)).ToList();

        if (userIdentities.Count == 0 || userIdentities.Count == authenticated.Count)
        {
            return Task.CompletedTask;
        }

        var actingUser = new ClaimsPrincipal(userIdentities);
        if (!requirement.AllowedRoles.Any(actingUser.IsInRole))
        {
            context.Fail(new AuthorizationFailureReason(
                this,
                $"The signed-in user holds none of the roles {string.Join(", ", requirement.AllowedRoles)}. " +
                "The API key sent with the request is not counted once a user token is present."));
        }

        return Task.CompletedTask;
    }
}
