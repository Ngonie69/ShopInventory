using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using ShopInventory.Controllers;
using ShopInventory.Models;

namespace ShopInventory.Tests;

/// <summary>
/// Van sales customers sign in to the customer ordering app as a subject of their own, holding
/// <see cref="ApplicationRoles.VanSalesCustomer"/> and nothing else.
///
/// What keeps a shopkeeper out of the staff API is not a check written on any endpoint — it is an
/// absence. The "ApiAccess" policy that guards nearly every controller is
/// <c>RequireRole(ApplicationRoles.ApiAccessRoles)</c>, so a role that is not in that array is
/// refused everywhere by default, including on endpoints nobody has written yet. That is the
/// property worth having: a new staff endpoint is safe from customers the moment it is created,
/// without the author needing to know this feature exists.
///
/// An absence is exactly what review does not catch. Someone tidying the role catalogue, or adding
/// the customer role to a list "for completeness", would hand every van sales customer the whole
/// staff API and the diff would look like housekeeping. Hence these tests: they fail loudly on the
/// change that would otherwise pass quietly.
/// </summary>
public sealed class VanSalesCustomerAuthPolicyTests
{
    [Fact]
    public void The_customer_role_is_not_an_api_access_role()
    {
        // The load-bearing assertion. If this fails, customers can reach the staff API.
        Assert.DoesNotContain(ApplicationRoles.VanSalesCustomer, ApplicationRoles.ApiAccessRoles);
    }

    [Fact]
    public void The_customer_role_is_not_an_api_access_with_operator_role()
    {
        Assert.DoesNotContain(
            ApplicationRoles.VanSalesCustomer,
            ApplicationRoles.ApiAccessWithOperatorRoles);
    }

    [Fact]
    public void The_customer_role_cannot_be_assigned_to_a_staff_user()
    {
        // User management must not be able to mint an employee holding the customer role: such an
        // account would be a staff login that the customer endpoints also trust.
        Assert.DoesNotContain(ApplicationRoles.VanSalesCustomer, ApplicationRoles.AssignableRoles);
        Assert.False(ApplicationRoles.IsAssignableRole(ApplicationRoles.VanSalesCustomer));
    }

    [Fact]
    public void The_customer_role_is_not_retainable_on_a_managed_user()
    {
        Assert.DoesNotContain(
            ApplicationRoles.VanSalesCustomer,
            ApplicationRoles.RetainableManagedRoles);
        Assert.False(ApplicationRoles.IsRetainableManagedRole(ApplicationRoles.VanSalesCustomer));
    }

    [Fact]
    public void The_customer_role_carries_no_staff_scope()
    {
        // These arrays drive required account attributes — warehouses, business partners, cost
        // centres — and membership would imply the customer role is a selling staff account.
        Assert.DoesNotContain(ApplicationRoles.VanSalesCustomer, ApplicationRoles.RouteCustomerScopedRoles);
        Assert.DoesNotContain(ApplicationRoles.VanSalesCustomer, ApplicationRoles.DepotLoadedRoles);
        Assert.False(ApplicationRoles.RequiresWarehouseAssignments(ApplicationRoles.VanSalesCustomer));
        Assert.False(ApplicationRoles.SupportsWarehouseAssignments(ApplicationRoles.VanSalesCustomer));
        Assert.False(ApplicationRoles.RequiresCustomerAssignments(ApplicationRoles.VanSalesCustomer));
    }

    [Fact]
    public void The_customer_endpoints_require_the_customer_policy_by_default()
    {
        // Stated at the class so anything added later inherits it. The three exceptions below are
        // the whole anonymous surface; a fourth added by accident is what this pair of tests exists
        // to catch.
        var authorize = typeof(VanSalesCustomerAuthController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .ToArray();

        Assert.Single(authorize);
        Assert.Equal("VanSalesCustomerAccess", authorize[0].Policy);
    }

    [Fact]
    public void Only_the_three_pre_session_endpoints_are_anonymous()
    {
        // Requesting and verifying a code happen before a session exists, and refresh exists
        // precisely to be callable once the access token has expired. Nothing else on this
        // controller has a reason to be reachable without one.
        var anonymous = typeof(VanSalesCustomerAuthController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any())
            .Select(m => m.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(["Refresh", "RequestOtp", "VerifyOtp"], anonymous);
    }

    [Fact]
    public void The_operator_account_screen_is_staff_only()
    {
        // Granting and withdrawing sign-ins decides who may order in a shop's name. A customer
        // reaching it could enrol themselves or cut off a rival shop.
        var authorize = typeof(VanSalesCustomerAccountsController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .ToArray();

        Assert.Single(authorize);
        Assert.Equal("ApiAccess", authorize[0].Policy);

        var anonymous = typeof(VanSalesCustomerAccountsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any())
            .ToArray();

        Assert.Empty(anonymous);
    }

    [Fact]
    public void The_customer_claim_names_are_stable()
    {
        // Written into the token by AuthService, required by the VanSalesCustomerAccess policy, and
        // read by every customer handler. The three only agree because they share these constants;
        // changing a value silently locks every existing app install out.
        Assert.Equal("vansales_customer_id", VanSalesCustomerClaims.AccountId);
        Assert.Equal("vansales_customer_code", VanSalesCustomerClaims.CustomerCode);
    }
}
