using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ShopInventory.Authentication;
using ShopInventory.Controllers;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Models;
using ShopInventory.Services;
using ShopInventory.Web.Data;

namespace ShopInventory.Tests;

/// <summary>
/// A sales rep raising a customer quotation, pinned across the three places that have to agree
/// before the workflow exists at all: the API's permission family, the Web's page gate, and the
/// notification audience.
/// </summary>
/// <remarks>
/// <see cref="QuotationController"/> was guarded by <c>invoices.*</c> — it had no permissions of
/// its own — so the only way to let a rep quote a customer was to hand them invoicing, which is a
/// different and much larger trust: <c>invoices.create</c> is the fiscalised sales path. The
/// quotation permissions exist to separate the two, and these tests state the separation rather
/// than the wiring, because the wiring is three files apart and each half looks correct alone.
///
/// The failure mode this guards is silent in both directions. Widen the page gate without the
/// permission and the rep opens the form, fills it in, and is refused on submit; grant the
/// permission without the page and nothing appears to have changed at all.
/// </remarks>
public sealed class QuotationAccessTests
{
    private static readonly string[] QuotationPermissions =
    [
        Permission.ViewQuotations,
        Permission.CreateQuotations,
        Permission.EditQuotations,
        Permission.DeleteQuotations
    ];

    [Fact]
    public void A_sales_rep_can_raise_a_quotation()
    {
        var permissions = Permission.GetDefaultPermissionsForRole(ApplicationRoles.SalesRep);

        Assert.Contains(Permission.ViewQuotations, permissions);
        Assert.Contains(Permission.CreateQuotations, permissions);

        // Edit covers reprice, approve, apply-standard-vat and status — a quote gets revised
        // before a customer accepts it, and the rep who raised it is who revises it.
        Assert.Contains(Permission.EditQuotations, permissions);
    }

    /// <summary>
    /// The whole point of the separate family. Quoting is an offer; invoicing is money, stock and a
    /// fiscal receipt. A rep converts an accepted quote into the sales order they could already
    /// raise, and somebody else invoices it.
    /// </summary>
    [Fact]
    public void Quoting_carries_no_invoice_rights()
    {
        var permissions = Permission.GetDefaultPermissionsForRole(ApplicationRoles.SalesRep);

        Assert.DoesNotContain(Permission.ViewInvoices, permissions);
        Assert.DoesNotContain(Permission.CreateInvoices, permissions);
        Assert.DoesNotContain(Permission.EditInvoices, permissions);
        Assert.DoesNotContain(Permission.DeleteInvoices, permissions);
        Assert.DoesNotContain(Permission.VoidInvoices, permissions);

        // Nor does the conversion, which the rep does hold: it lands on /api/SalesOrder.
        Assert.Contains(Permission.CreateSalesOrders, permissions);
    }

    /// <summary>
    /// Stated over every action rather than the ones changed, because the next one added is the one
    /// that gets copied from the invoice controller with its permission attached.
    /// </summary>
    [Fact]
    public void Every_quotation_endpoint_is_guarded_by_a_quotation_permission()
    {
        var actions = typeof(QuotationController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .ToArray();

        Assert.NotEmpty(actions);

        foreach (var action in actions)
        {
            var required = action
                .GetCustomAttributes<RequirePermissionAttribute>(inherit: true)
                .SelectMany(attribute => attribute.RequiredPermissions)
                .ToArray();

            Assert.True(
                required.Length > 0,
                $"{action.Name} carries no [RequirePermission], so it is open to every role in the " +
                "ApiAccess policy — which includes Driver, PodOperator and every handset role.");

            var strays = required.Except(QuotationPermissions, StringComparer.Ordinal).ToArray();

            Assert.True(
                strays.Length == 0,
                $"{action.Name} is guarded by {string.Join(", ", strays)} rather than a quotations.* " +
                "permission. A quotation endpoint behind invoices.* is only reachable by handing " +
                "invoicing to whoever needs to quote.");
        }
    }

    /// <summary>
    /// The cross-project pin. The pages are gated by role and the API by permission, and neither
    /// side can see the other; a role on one list and not the other is a form that submits into a
    /// 403.
    /// </summary>
    [Theory]
    [InlineData(UserRoles.Admin)]
    [InlineData(UserRoles.Cashier)]
    [InlineData(UserRoles.SalesRep)]
    public void Every_role_that_can_open_the_quotation_pages_can_use_the_api(string role)
    {
        Assert.Contains(role, UserRoles.QuotationRoles.Split(','));

        var permissions = Permission.GetDefaultPermissionsForRole(role);

        Assert.Contains(Permission.ViewQuotations, permissions);
        Assert.Contains(Permission.CreateQuotations, permissions);
    }

    [Fact]
    public void The_page_gate_names_no_role_the_api_would_refuse()
    {
        var refused = UserRoles.QuotationRoles
            .Split(',')
            .Where(role => !Permission.GetDefaultPermissionsForRole(role).Contains(Permission.CreateQuotations))
            .ToArray();

        Assert.True(
            refused.Length == 0,
            $"{string.Join(", ", refused)} can open /quotations/create but cannot post one.");
    }

    /// <summary>
    /// Every role that could quote before the family was split still can. The permissions moved;
    /// nobody who was working was meant to stop.
    /// </summary>
    [Theory]
    [InlineData(ApplicationRoles.Cashier)]
    [InlineData(ApplicationRoles.Manager)]
    public void The_roles_that_could_already_quote_still_can(string role)
    {
        var permissions = Permission.GetDefaultPermissionsForRole(role);

        Assert.Contains(Permission.ViewQuotations, permissions);
        Assert.Contains(Permission.CreateQuotations, permissions);
        Assert.Contains(Permission.EditQuotations, permissions);
    }

    /// <summary>
    /// Admin holds every permission by construction, which is what keeps the delete action — granted
    /// to no other role, then or now — reachable.
    /// </summary>
    [Fact]
    public void The_quotation_permissions_are_in_the_catalogue()
    {
        var all = Permission.GetAllPermissions();

        foreach (var permission in QuotationPermissions)
        {
            // Not decoration: UpdateUserPermissionsAsync rejects anything absent from this list, so a
            // permission missing here cannot be granted to a user at all, and the page that offers
            // the checkbox reads the same catalogue.
            Assert.Contains(permission, all);
        }

        Assert.Contains(Permission.DeleteQuotations, Permission.GetDefaultPermissionsForRole(ApplicationRoles.Admin));
    }

    /// <summary>
    /// A rep who raises a quotation is told when it posts. The notification's audience is the
    /// intersection of its category and its /quotations action URL, so the category being a sales
    /// one is not enough on its own — the route list has to name the rep too.
    /// </summary>
    [Fact]
    public void A_quotation_notification_reaches_a_sales_rep()
    {
        var audience = NotificationAudienceRules.GetBroadcastAudienceRoles("Quotation", "/quotations/1");

        Assert.Contains(ApplicationRoles.SalesRep, audience);
        Assert.Contains(ApplicationRoles.Cashier, audience);
        Assert.Contains(ApplicationRoles.Admin, audience);
    }

    /// <summary>
    /// What the compiled pages carry, read off the built component types rather than the .razor
    /// source. The attribute goes through Razor codegen, and a page that opens for the wrong set of
    /// roles looks identical in review.
    /// </summary>
    [Theory]
    [InlineData("ShopInventory.Web.Components.Pages.Quotations")]
    [InlineData("ShopInventory.Web.Components.Pages.CreateQuotation")]
    public void The_quotation_pages_are_gated_by_the_quotation_roles(string typeName)
    {
        var page = typeof(UserRoles).Assembly.GetType(typeName);
        Assert.NotNull(page);

        var authorize = page!.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToArray();

        Assert.Single(authorize);
        Assert.Equal(UserRoles.QuotationRoles, authorize[0].Roles);
        Assert.Contains(UserRoles.SalesRep, authorize[0].Roles!.Split(','));
    }

    /// <summary>
    /// The filter itself, run against the permission set a sales rep actually resolves to. The tests
    /// above pin the two lists; this one is the pair meeting — a rep posting a quotation is let
    /// through, and a role with neither family is not.
    /// </summary>
    /// <remarks>
    /// Worth running rather than reasoning about, because the answer for a role that holds nothing
    /// is silent: <see cref="RequirePermissionAttribute"/> sets a <see cref="ForbidResult"/> on the
    /// context instead of throwing, so an endpoint guarded by a permission nobody holds looks
    /// exactly like one that works until somebody signs in as that role.
    /// </remarks>
    [Theory]
    [InlineData(ApplicationRoles.SalesRep, true)]
    [InlineData(ApplicationRoles.Cashier, true)]
    [InlineData(ApplicationRoles.Manager, true)]
    [InlineData(ApplicationRoles.Driver, false)]
    [InlineData(ApplicationRoles.PodOperator, false)]
    public async Task The_create_permission_filter_answers_per_role(string role, bool allowed)
    {
        var context = FilterContextFor(role);

        await new RequirePermissionAttribute(Permission.CreateQuotations).OnAuthorizationAsync(context);

        if (allowed)
        {
            Assert.Null(context.Result);
        }
        else
        {
            Assert.IsType<ForbidResult>(context.Result);
        }
    }

    /// <summary>
    /// The half that used to be true of every role holding <c>invoices.view</c>. A driver reads
    /// invoices for proof of delivery and could list every customer quotation on the strength of
    /// it; splitting the family is what closed that, so it is stated rather than left implied.
    /// </summary>
    [Fact]
    public async Task A_driver_can_no_longer_list_quotations()
    {
        var driver = Permission.GetDefaultPermissionsForRole(ApplicationRoles.Driver);
        Assert.Contains(Permission.ViewInvoices, driver);

        var context = FilterContextFor(ApplicationRoles.Driver);

        await new RequirePermissionAttribute(Permission.ViewQuotations).OnAuthorizationAsync(context);

        Assert.IsType<ForbidResult>(context.Result);
    }

    private static AuthorizationFilterContext FilterContextFor(string role)
    {
        var userId = Guid.NewGuid();

        var services = new ServiceCollection();
        services.AddSingleton<IUserManagementService>(
            new RoleDefaultPermissionsService(Permission.GetDefaultPermissionsForRole(role)));

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, role)],
            authenticationType: "Test");

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            RequestServices = services.BuildServiceProvider()
        };

        return new AuthorizationFilterContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            []);
    }

    /// <summary>
    /// Answers effective permissions from a fixed list and nothing else. Every other member throws:
    /// the filter calls exactly one, and a stub that quietly returned defaults elsewhere would hide
    /// a change in which call it makes.
    /// </summary>
    private sealed class RoleDefaultPermissionsService(List<string> permissions) : IUserManagementService
    {
        public Task<List<string>> GetEffectivePermissionsAsync(Guid userId) => Task.FromResult(permissions);

        public Task<PagedResult<UserDetailDto>> GetUsersAsync(int page = 1, int pageSize = 10, string? search = null, string? role = null, bool? isActive = null) => throw new NotSupportedException();
        public Task<UserDetailDto?> GetUserByIdAsync(Guid userId) => throw new NotSupportedException();
        public Task<ServiceResult<UserDetailDto>> CreateUserAsync(CreateUserDetailRequest request) => throw new NotSupportedException();
        public Task<ServiceResult> DeleteUserAsync(Guid userId) => throw new NotSupportedException();
        public Task<UserPermissionsResponse?> GetUserPermissionsAsync(Guid userId) => throw new NotSupportedException();
        public Task<ServiceResult> UpdateUserPermissionsAsync(Guid userId, UpdatePermissionsRequest request) => throw new NotSupportedException();
        public AvailablePermissionsResponse GetAvailablePermissions() => throw new NotSupportedException();
        public Task<bool> HasPermissionAsync(Guid userId, string permission) => throw new NotSupportedException();
        public Task<ServiceResult> UnlockUserAsync(Guid userId) => throw new NotSupportedException();
        public Task<ServiceResult> ResetTwoFactorAsync(Guid userId) => throw new NotSupportedException();
    }
}
