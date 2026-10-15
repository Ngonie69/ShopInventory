using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ShopInventory.Authentication;
using ShopInventory.Configuration;
using ShopInventory.Controllers;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCustomerAuth;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// A van sales customer's token is signed with the staff key, issuer and audience, so it passes the
/// API's JWT validation. These hold that it still reaches nothing a bare <c>[Authorize]</c> guards,
/// and that the staff password change in particular cannot be pointed at an account by name.
/// </summary>
public sealed class CustomerTokenOnStaffEndpointTests
{
    private static readonly JwtSettings Jwt = new()
    {
        SecretKey = new string('k', 64),
        Issuer = "ShopInventoryAPI",
        Audience = "ShopInventoryClients",
        AccessTokenExpirationMinutes = 60,
        RefreshTokenExpirationDays = 7
    };

    [Fact]
    public async Task No_bare_authorize_action_admits_a_customer_token()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApiAuthorizationPolicies();
        services.AddStaffDefaultAuthorizationPolicy();
        services.AddPermissionAuthorization();
        await using var provider = services.BuildServiceProvider();
        var policies = provider.GetRequiredService<IAuthorizationPolicyProvider>();
        var authorization = provider.GetRequiredService<IAuthorizationService>();

        var customer = CustomerPrincipal();
        var staff = StaffPrincipal();

        var defaultPolicyActions = BareAuthorizeActions().ToList();

        // The ten this was found on; a sweep that found none would prove nothing.
        Assert.Contains("PasswordController.ChangePassword", defaultPolicyActions.Select(a => a.Name));
        Assert.True(defaultPolicyActions.Count >= 10, string.Join(", ", defaultPolicyActions.Select(a => a.Name)));

        var admitted = new List<string>();
        foreach (var (name, authorizeData) in defaultPolicyActions)
        {
            var policy = await AuthorizationPolicy.CombineAsync(policies, authorizeData);
            Assert.NotNull(policy);

            if ((await authorization.AuthorizeAsync(customer, policy!)).Succeeded)
                admitted.Add(name);

            // The control: a member of staff is still let in.
            Assert.True((await authorization.AuthorizeAsync(staff, policy!)).Succeeded, name);
        }

        Assert.Empty(admitted);
    }

    [Fact]
    public async Task The_customer_endpoints_still_admit_a_customer_token()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApiAuthorizationPolicies();
        services.AddStaffDefaultAuthorizationPolicy();
        await using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();

        var result = await authorization.AuthorizeAsync(CustomerPrincipal(), "VanSalesCustomerAccess");

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task A_password_change_without_a_staff_user_id_is_refused_whatever_username_it_names()
    {
        var mediator = StubProxy.Unused<IMediator>();
        var controller = new PasswordController(mediator)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CustomerPrincipal() }
            }
        };

        var result = await controller.ChangePassword(
            new ChangePasswordRequest
            {
                Username = "admin",
                CurrentPassword = "guess-1",
                NewPassword = "Copper-kettle-552!"
            },
            CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    /// <summary>
    /// A principal exactly as the API sees a customer: a token from the real issuer, read back with
    /// the validation parameters Program.cs uses.
    /// </summary>
    private static ClaimsPrincipal CustomerPrincipal()
    {
        var issuer = new VanSalesCustomerTokenIssuer(
            Options.Create(Jwt),
            Options.Create(new VanSalesCustomerAuthSettings()));

        var (token, _) = issuer.IssueAccessToken(
            new VanSalesCustomerAccountEntity { Id = 42, PhoneE164 = "+263772000111" },
            "C00042");

        return new JwtSecurityTokenHandler().ValidateToken(
            token,
            new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = Jwt.Issuer,
                ValidAudience = Jwt.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Jwt.SecretKey))
            },
            out _);
    }

    private static ClaimsPrincipal StaffPrincipal() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Name, "wellington.moyo"),
                new Claim(ClaimTypes.Role, ApplicationRoles.SalesRep)
            ],
            "Bearer"));

    /// <summary>
    /// Every action whose authorization comes down to the default policy: an [Authorize] somewhere
    /// on it or its controller, none naming a policy or roles, and no [AllowAnonymous].
    /// </summary>
    private static IEnumerable<(string Name, IReadOnlyList<IAuthorizeData> AuthorizeData)> BareAuthorizeActions()
    {
        var controllers = typeof(PasswordController).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract);

        foreach (var controller in controllers)
        {
            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (action.IsDefined(typeof(NonActionAttribute), true)
                    || action.IsDefined(typeof(AllowAnonymousAttribute), true)
                    || controller.IsDefined(typeof(AllowAnonymousAttribute), true))
                    continue;

                var authorizeData = controller.GetCustomAttributes(true).OfType<IAuthorizeData>()
                    .Concat(action.GetCustomAttributes(true).OfType<IAuthorizeData>())
                    .ToList();

                if (authorizeData.Count == 0)
                    continue;

                if (authorizeData.Any(data => !string.IsNullOrWhiteSpace(data.Policy) || !string.IsNullOrWhiteSpace(data.Roles)))
                    continue;

                yield return ($"{controller.Name}.{action.Name}", authorizeData);
            }
        }
    }
}
