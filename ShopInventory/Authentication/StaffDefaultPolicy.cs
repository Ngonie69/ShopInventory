using Microsoft.AspNetCore.Authorization;
using ShopInventory.Models;

namespace ShopInventory.Authentication;

/// <summary>
/// The policy a bare <c>[Authorize]</c> means on this API: a signed-in member of staff.
/// </summary>
/// <remarks>
/// Van sales customer tokens are signed with the staff key, issuer and audience, so they pass JWT
/// validation like any staff token. Every named policy and role gate refuses them, because the
/// customer role is in none of the staff role lists, but the framework's default policy asks only
/// that the caller be authenticated. That let a customer token through every bare <c>[Authorize]</c>
/// action, including the staff password change. Here the default refuses any caller carrying the
/// customer role or the customer claims, so a bare <c>[Authorize]</c> written later is staff-only
/// without its author having to know customers exist.
/// </remarks>
public static class StaffDefaultPolicy
{
    public static AuthorizationPolicy Build() =>
        new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(context => !IsVanSalesCustomer(context.User))
            .Build();

    public static IServiceCollection AddStaffDefaultAuthorizationPolicy(this IServiceCollection services)
    {
        services.Configure<AuthorizationOptions>(options => options.DefaultPolicy = Build());
        return services;
    }

    private static bool IsVanSalesCustomer(System.Security.Claims.ClaimsPrincipal user) =>
        user.IsInRole(ApplicationRoles.VanSalesCustomer)
        || user.HasClaim(claim => claim.Type is VanSalesCustomerClaims.AccountId or VanSalesCustomerClaims.CustomerCode);
}
