using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using ShopInventory.Models;

namespace ShopInventory.Authentication;

/// <summary>
/// The API's named authorization policies and the handler that judges their role gates, registered from
/// one place so tests evaluate the same definitions Program.cs runs rather than a copy of them.
/// </summary>
/// <remarks>
/// "AdminOnly" is not defined here. It is the system-admin permission policy
/// <see cref="PermissionAuthorizationExtensions.AddPermissionAuthorization"/> registers. A role-gate
/// definition of the same name used to sit here as well, but Program.cs registers the permission policies
/// later and <c>AddPolicy</c> replaces a name, so it never ran. scripts/inventory_role_gates.py fails on a
/// policy name registered twice.
/// </remarks>
public static class ApiAuthorizationPolicies
{
    public static IServiceCollection AddApiAuthorizationPolicies(this IServiceCollection services)
    {
        // Every [Authorize(Roles = ...)] and RequireRole is judged by the signed-in user when a request
        // carries an API key and a user token together, not by the key's roles.
        services.AddSingleton<IAuthorizationHandler, ActingUserRoleAuthorizationHandler>();

        // Configure Authorization with policy supporting both JWT and API Key
        services.AddAuthorization(options =>
        {
            options.AddPolicy("ApiAccess", policy =>
                policy.RequireRole(ApplicationRoles.ApiAccessRoles)
                      .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, AuthenticationSchemes.ApiKey));
            options.AddPolicy("ApiAccessWithOperator", policy =>
                policy.RequireRole(ApplicationRoles.ApiAccessWithOperatorRoles)
                      .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, AuthenticationSchemes.ApiKey));

            // Van sales customers ordering on their own phones. A separate subject from staff, so a
            // separate policy — and JWT only, deliberately: the API key scheme authenticates
            // integrations, and an integration key must not be able to act as a customer.
            //
            // The customer code claim is required as well as the role. A token with the role but no
            // customer on it cannot be resolved to an account, and an endpoint that read the customer
            // from the request body instead would be an IDOR; requiring the claim here means every
            // handler downstream can take the identity from the token and nothing else.
            options.AddPolicy("VanSalesCustomerAccess", policy =>
                policy.RequireRole(ApplicationRoles.VanSalesCustomer)
                      .RequireClaim(VanSalesCustomerClaims.CustomerCode)
                      .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme));
        });

        return services;
    }
}
