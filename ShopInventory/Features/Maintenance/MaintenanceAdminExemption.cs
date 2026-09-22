using System.Security.Claims;
using ShopInventory.Authentication;
using ShopInventory.Models;

namespace ShopInventory.Features.Maintenance;

/// <summary>
/// Whether the person behind a web portal request keeps working while the portal is frozen.
/// </summary>
/// <remarks>
/// <para>
/// Freezing the portal without this would freeze the people doing the maintenance. Somebody has to
/// be able to correct the row that caused the problem, re-post the document that failed, and see
/// whether the thing they are fixing is fixed — and they are the same people who threw the switch.
/// So Admins are exempt and everybody else is stopped.
/// </para>
/// <para>
/// The trap here is <c>ClaimsPrincipal.IsInRole</c>. The Web sends its <c>X-API-Key</c> and the
/// signed-in user's token on the same request, and the key's own identity carries Admin, so asking
/// the merged principal answers "yes, Admin" for a cashier — the bug that let every Web user
/// through a permission check in September. The question is therefore asked of the user's
/// identities alone, exactly as <see cref="ActingUserRoleAuthorizationHandler"/> asks it for role
/// gates.
/// </para>
/// <para>
/// A request carrying no user identity at all is not exempt. That is the Web's background cache
/// sweeps, which authenticate with the key alone: nobody is waiting on them, and a sweep rebuilding
/// a cache from a database under maintenance is one of the things worth stopping.
/// </para>
/// </remarks>
public static class MaintenanceAdminExemption
{
    public static bool AppliesTo(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return false;
        }

        var userIdentities = principal.Identities
            .Where(identity => identity.IsAuthenticated && !ApiKeyServiceCaller.IsApiKeyIdentity(identity))
            .ToList();

        if (userIdentities.Count == 0)
        {
            return false;
        }

        return new ClaimsPrincipal(userIdentities).IsInRole(ApplicationRoles.Admin);
    }
}
