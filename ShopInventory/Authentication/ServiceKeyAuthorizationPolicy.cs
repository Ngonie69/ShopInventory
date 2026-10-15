using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using ShopInventory.Models;

namespace ShopInventory.Authentication;

/// <summary>
/// The "ServiceKeyOnly" policy: an integration calling as itself with its API key, and nobody else.
/// </summary>
/// <remarks>
/// For callbacks that report facts the API then trusts — a fiscal device's transaction, a SAP stock
/// transfer the TransferEventListener saw. Under "ApiAccess" any staff login could post one, so a Lab
/// or Driver account could mark a sale fiscalised or move stock on the local ledger.
///
/// Both schemes authenticate, as they do for "ApiAccess", and the request must then be a service call
/// in <see cref="ApiKeyServiceCaller.IsServiceCall"/>'s sense: an Admin or ApiUser key and no other
/// identity. So a staff token is refused alone and refused beside the Web's key, where the user, not
/// the key, would be the one acting — the same line RequirePermission draws. Authenticating the key
/// scheme alone would have ignored a token sent beside it and let any Web user through on the key.
/// RequireRole is kept beside the assertion so the role-gate inventories can read who is admitted.
///
/// Registered on its own rather than beside "ApiAccess" in <see cref="ApiAuthorizationPolicies"/> so
/// the two can change independently; scripts/inventory_role_gates.py fails if a name is registered
/// twice, so the split cannot hide a duplicate. Attributes name it as the literal, as they do
/// "ApiAccess": that script resolves policies by their literal names.
/// </remarks>
public static class ServiceKeyAuthorizationPolicy
{
    public static IServiceCollection AddServiceKeyAuthorizationPolicy(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.AddPolicy("ServiceKeyOnly", policy =>
                policy.RequireRole(ApplicationRoles.Admin, ApplicationRoles.ApiUser)
                      .RequireAssertion(context => ApiKeyServiceCaller.IsServiceCall(context.User))
                      .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, AuthenticationSchemes.ApiKey));
        });

        return services;
    }
}
