using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using ShopInventory.Authentication;
using ShopInventory.Configuration;
using ShopInventory.Controllers;
using ShopInventory.Models;
using ShopInventory.Services;
using Xunit.Abstractions;

namespace ShopInventory.Tests;

/// <summary>
/// A role gate on a request carrying an API key and a user's token together answers for the user.
/// </summary>
/// <remarks>
/// ShopInventory.Web sends its <c>X-API-Key</c> and the signed-in user's JWT on every call. The
/// "ApiAccess" and "ApiAccessWithOperator" policies authenticate both schemes, so the principal holds
/// the key's identity (Admin, ApiUser) beside the user's, and ASP.NET Core's role check asks the whole
/// principal: every <c>[Authorize(Roles = ...)]</c> passed for any Web user on the key's Admin.
///
/// Nothing here hand-assembles that principal or restates a policy. Each case runs the real
/// <see cref="ApiKeyAuthenticationHandler"/> and JWT bearer handler, combines the action's own
/// <c>[Authorize]</c> attributes with the named policies exactly as Program.cs registers them, and
/// evaluates the result through <see cref="PolicyEvaluator"/> — the steps the authorization middleware
/// takes — so a gate added or changed on a controller is evaluated here as it is written.
///
/// Every evaluation runs in its own service scope, as a request does. Authentication handlers cache
/// their result for the request; resolved from the root provider they would answer every later
/// evaluation with the first one's user.
/// </remarks>
public sealed class ApiKeyWithUserTokenRoleGateTests(ITestOutputHelper output)
{
    private const string WebApiKey = "web-app-test-key";
    private const string Issuer = "ShopInventory.Tests";
    private const string Audience = "ShopInventory.Tests.Clients";

    private static readonly string[] WebKeyRoles = [ApplicationRoles.Admin, ApplicationRoles.ApiUser];

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("api-key-with-user-token-role-gate-tests-signing-key-0123456789"));

    /// <summary>The customer portal signs its own tokens with a secret the API does not hold.</summary>
    private static readonly SymmetricSecurityKey PortalSigningKey =
        new(Encoding.UTF8.GetBytes("customer-portal-signing-key-a-different-secret-9876543210"));

    /// <summary>The role each issued token's user holds, for the permission lookups some policies make.</summary>
    private static readonly ConcurrentDictionary<Guid, string> IssuedRoles = new();

    private static MethodInfo CreateInvoice => Action<InvoiceController>(nameof(InvoiceController.CreateInvoice));

    private static MethodInfo CreateNotification => Action<NotificationController>(nameof(NotificationController.CreateNotification));

    private static MethodInfo CreateTransferDirect => Action<DesktopIntegrationController>(nameof(DesktopIntegrationController.CreateTransferDirect));

    /// <summary>
    /// The reported case. The merged principal really does answer Admin, which is why the framework's
    /// own check let it through; the gate refuses anyway because the merchandiser is the one acting.
    /// </summary>
    [Fact]
    public async Task A_web_merchandiser_is_refused_an_Admin_or_Cashier_gate()
    {
        await using var services = BuildServices();

        var outcome = await EvaluateAsync(services, CreateInvoice, WebApiKey, UserToken(ApplicationRoles.Merchandiser));

        Assert.Equal(2, outcome.User.Identities.Count(identity => identity.IsAuthenticated));
        Assert.True(outcome.User.IsInRole(ApplicationRoles.Admin), "the key's Admin role is on the merged principal");
        Assert.True(outcome.Result.Forbidden, "a merchandiser is not an Admin or a Cashier");
    }

    [Fact]
    public async Task A_web_merchandiser_is_refused_an_Admin_only_action()
    {
        await using var services = BuildServices();

        var outcome = await EvaluateAsync(services, CreateNotification, WebApiKey, UserToken(ApplicationRoles.Merchandiser));

        Assert.True(outcome.Result.Forbidden);
    }

    [Fact]
    public async Task A_web_user_holding_one_of_the_roles_passes()
    {
        await using var services = BuildServices();

        var outcome = await EvaluateAsync(services, CreateInvoice, WebApiKey, UserToken(ApplicationRoles.Cashier));

        Assert.True(outcome.Result.Succeeded);
    }

    /// <summary>
    /// The negative control. A real Admin signed in on the Web holds the role themselves, so nothing
    /// they could do before is refused now.
    /// </summary>
    [Fact]
    public async Task A_real_admin_on_the_web_still_passes()
    {
        await using var services = BuildServices();

        foreach (var action in new[] { CreateInvoice, CreateNotification, CreateTransferDirect })
        {
            var outcome = await EvaluateAsync(services, action, WebApiKey, UserToken(ApplicationRoles.Admin));
            Assert.True(outcome.Result.Succeeded, $"{action.DeclaringType!.Name}.{action.Name}");
        }
    }

    /// <summary>
    /// The class-level policy is a role gate too. A van sales customer's token is refused the staff
    /// API on its own; the key sent beside it must not carry it in.
    /// </summary>
    [Fact]
    public async Task The_ApiAccess_policy_is_judged_by_the_user_as_well()
    {
        await using var services = BuildServices();
        var apiAccess = await NamedPolicyAsync(services, "ApiAccess");

        var customer = await EvaluateAsync(services, apiAccess, WebApiKey, VanSalesCustomerToken());
        var merchandiser = await EvaluateAsync(services, apiAccess, WebApiKey, UserToken(ApplicationRoles.Merchandiser));

        Assert.True(customer.Result.Forbidden, "a van sales customer is not staff");
        Assert.True(merchandiser.Result.Succeeded, "a merchandiser is staff");
    }

    /// <summary>
    /// Integrations that call with the key and nothing else — the transfer event listener, the Web's
    /// background jobs — keep the key's roles.
    /// </summary>
    [Fact]
    public async Task A_key_calling_alone_keeps_its_roles()
    {
        await using var services = BuildServices();

        foreach (var action in new[] { CreateInvoice, CreateNotification, CreateTransferDirect })
        {
            var outcome = await EvaluateAsync(services, action, WebApiKey, bearerToken: null);
            Assert.True(outcome.Result.Succeeded, $"{action.DeclaringType!.Name}.{action.Name}");
        }
    }

    /// <summary>
    /// The customer portal's pages call the API through the Web's key. A portal token signed with its
    /// own secret fails the API's bearer scheme, so the request is the key's alone and is judged that way.
    /// </summary>
    [Fact]
    public async Task A_key_beside_a_token_the_api_cannot_validate_is_judged_as_the_key()
    {
        await using var services = BuildServices();
        var portalToken = UserToken("Customer", PortalSigningKey);

        var outcome = await EvaluateAsync(services, CreateInvoice, WebApiKey, portalToken);

        Assert.Equal(1, outcome.User.Identities.Count(identity => identity.IsAuthenticated));
        Assert.True(outcome.Result.Succeeded);
    }

    /// <summary>Handsets, tills and the customer app send no key, and answer as they always did.</summary>
    [Fact]
    public async Task A_token_without_a_key_is_judged_as_before()
    {
        await using var services = BuildServices();

        var merchandiser = await EvaluateAsync(services, CreateInvoice, apiKey: null, UserToken(ApplicationRoles.Merchandiser));
        var cashier = await EvaluateAsync(services, CreateInvoice, apiKey: null, UserToken(ApplicationRoles.Cashier));
        var customer = await EvaluateAsync(
            services, await NamedPolicyAsync(services, "VanSalesCustomerAccess"), apiKey: null, VanSalesCustomerToken());

        Assert.True(merchandiser.Result.Forbidden, "merchandiser");
        Assert.True(cashier.Result.Succeeded, "cashier");
        Assert.True(customer.Result.Succeeded, "van sales customer");
    }

    /// <summary>
    /// "AdminOnly" is defined twice — as a role gate over both schemes, and as the system-admin
    /// permission — and the permission definition is registered later and replaces the other. The
    /// endpoints named "AdminOnly" are therefore judged by the signed-in user's permissions, from the
    /// default bearer scheme only, and the key never reaches them.
    /// </summary>
    [Fact]
    public async Task The_AdminOnly_policy_is_the_system_admin_permission_and_never_sees_the_key()
    {
        await using var services = BuildServices();

        var adminOnly = await NamedPolicyAsync(services, "AdminOnly");

        Assert.Empty(adminOnly.AuthenticationSchemes);
        var requirement = Assert.IsType<PermissionRequirement>(Assert.Single(adminOnly.Requirements));
        Assert.Equal([Permission.SystemAdmin], requirement.Permissions);
    }

    /// <summary>
    /// Every role-gated action the key can reach, rather than a hand-picked few. For each one, a Web user
    /// holding none of its roles is refused. Where roles alone decide, a Web user holding one passes, a
    /// real Admin passes wherever Admin is named, and the key on its own gets exactly what its own roles
    /// allow. An action that also names a permission is checked for the refusal only, since the
    /// permission can refuse the others for reasons of its own.
    /// </summary>
    [Fact]
    public async Task Every_role_gated_action_answers_for_the_web_user_and_not_the_key()
    {
        await using var services = BuildServices();
        var staffRoles = ApplicationRoles.ApiAccessWithOperatorRoles
            .Where(role => role is not (ApplicationRoles.Admin or ApplicationRoles.ApiUser))
            .ToArray();

        var failures = new List<string>();
        var evaluated = new List<string>();
        var judgedByMoreThanRoles = new List<string>();
        var notReachableByKey = new List<string>();

        foreach (var action in ControllerActions())
        {
            var name = $"{action.DeclaringType!.Name}.{action.Name}";
            var policy = await EndpointPolicyAsync(services, action);
            var gates = policy.Requirements.OfType<RolesAuthorizationRequirement>()
                .Select(requirement => requirement.AllowedRoles.ToArray())
                .ToArray();

            if (gates.Length == 0)
            {
                continue;
            }

            if (!policy.AuthenticationSchemes.Contains(AuthenticationSchemes.ApiKey))
            {
                notReachableByKey.Add(name);
                continue;
            }

            evaluated.Add(name);
            bool PassesEveryGate(string role) => gates.All(gate => gate.Contains(role));

            var outsider = staffRoles.FirstOrDefault(role => !PassesEveryGate(role)) ?? ApplicationRoles.VanSalesCustomer;
            var refused = await EvaluateAsync(services, action, WebApiKey, UserToken(outsider));
            if (!refused.Result.Forbidden)
            {
                failures.Add($"{name}: key + {outsider} token was not refused");
            }

            if (!policy.Requirements.All(requirement =>
                    requirement is RolesAuthorizationRequirement or DenyAnonymousAuthorizationRequirement))
            {
                judgedByMoreThanRoles.Add(name);
                continue;
            }

            var insider = staffRoles.FirstOrDefault(PassesEveryGate);
            if (insider is not null)
            {
                var outcome = await EvaluateAsync(services, action, WebApiKey, UserToken(insider));
                if (!outcome.Result.Succeeded)
                {
                    failures.Add($"{name}: key + {insider} token was refused");
                }
            }

            if (PassesEveryGate(ApplicationRoles.Admin))
            {
                var outcome = await EvaluateAsync(services, action, WebApiKey, UserToken(ApplicationRoles.Admin));
                if (!outcome.Result.Succeeded)
                {
                    failures.Add($"{name}: key + Admin token was refused");
                }
            }

            var keyAlone = await EvaluateAsync(services, action, WebApiKey, bearerToken: null);
            var keyShouldPass = gates.All(gate => gate.Intersect(WebKeyRoles).Any());
            if (keyAlone.Result.Succeeded != keyShouldPass)
            {
                failures.Add($"{name}: the key alone {(keyAlone.Result.Succeeded ? "passed" : "was refused")}");
            }
        }

        output.WriteLine($"Evaluated {evaluated.Count} role-gated actions the key can reach.");
        output.WriteLine($"Also judged by a permission (refusal checked only): {string.Join(", ", judgedByMoreThanRoles)}");
        output.WriteLine($"Role-gated actions the key cannot reach: {string.Join(", ", notReachableByKey)}");
        foreach (var failure in failures)
        {
            output.WriteLine(failure);
        }

        Assert.Contains("InvoiceController.CreateInvoice", evaluated);
        Assert.Contains("NotificationController.CreateNotification", evaluated);
        Assert.Contains("DesktopIntegrationController.CreateTransferDirect", evaluated);
        Assert.Empty(failures);
    }

    private sealed record Outcome(PolicyAuthorizationResult Result, ClaimsPrincipal User);

    private static async Task<Outcome> EvaluateAsync(ServiceProvider services, MethodInfo action, string? apiKey, string? bearerToken) =>
        await EvaluateAsync(services, await EndpointPolicyAsync(services, action), apiKey, bearerToken);

    /// <summary>What UseAuthentication and the authorization middleware do with one request.</summary>
    private static async Task<Outcome> EvaluateAsync(
        ServiceProvider services,
        AuthorizationPolicy policy,
        string? apiKey,
        string? bearerToken)
    {
        await using var request = services.CreateAsyncScope();
        var httpContext = new DefaultHttpContext { RequestServices = request.ServiceProvider };

        if (apiKey is not null)
        {
            httpContext.Request.Headers["X-API-Key"] = apiKey;
        }

        if (bearerToken is not null)
        {
            httpContext.Request.Headers.Authorization = $"Bearer {bearerToken}";
        }

        var defaultAuthentication = await httpContext.AuthenticateAsync();
        if (defaultAuthentication.Succeeded)
        {
            httpContext.User = defaultAuthentication.Principal!;
        }

        var evaluator = new PolicyEvaluator(request.ServiceProvider.GetRequiredService<IAuthorizationService>());
        var authentication = await evaluator.AuthenticateAsync(policy, httpContext);
        var result = await evaluator.AuthorizeAsync(policy, authentication, httpContext, resource: null);

        return new Outcome(result, httpContext.User);
    }

    /// <summary>The controller's and the action's authorize data combined, as MVC combines them.</summary>
    private static async Task<AuthorizationPolicy> EndpointPolicyAsync(ServiceProvider services, MethodInfo action)
    {
        var authorizeData = action.DeclaringType!.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>()
            .Concat(action.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>());

        return await AuthorizationPolicy.CombineAsync(
                services.GetRequiredService<IAuthorizationPolicyProvider>(), authorizeData)
            ?? throw new InvalidOperationException($"{action.DeclaringType.Name}.{action.Name} has no authorization.");
    }

    private static async Task<AuthorizationPolicy> NamedPolicyAsync(ServiceProvider services, string name) =>
        await services.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(name)
        ?? throw new InvalidOperationException($"No policy named {name}.");

    private static IEnumerable<MethodInfo> ControllerActions() =>
        typeof(InvoiceController).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
            .Where(type => !type.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any())
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any())
            .Where(method => !method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any())
            .OrderBy(method => method.DeclaringType!.Name, StringComparer.Ordinal)
            .ThenBy(method => method.Name, StringComparer.Ordinal);

    private static MethodInfo Action<TController>(string name) =>
        typeof(TController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(method => method.Name == name);

    /// <summary>The authentication and authorization Program.cs registers, in the order it registers them.</summary>
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(StubProxy.For<IUserManagementService>((method, args) =>
            method.Name == nameof(IUserManagementService.GetEffectivePermissionsAsync) && IssuedRoles.TryGetValue((Guid)args![0]!, out var role)
                ? Task.FromResult(Permission.GetDefaultPermissionsForRole(role))
                : throw new InvalidOperationException($"IUserManagementService.{method.Name} was not expected to be called.")));
        services.AddSingleton(StubProxy.For<IAuthService>((method, args) =>
            method.Name == nameof(IAuthService.ValidateApiKey) && (string?)args![0] == WebApiKey
                ? new ApiKeyConfig { Key = WebApiKey, Name = "ShopInventory.Web", Roles = [.. WebKeyRoles] }
                : throw new InvalidOperationException($"IAuthService.{method.Name} was not expected to be called.")));

        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options => options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = Issuer,
                ValidAudience = Audience,
                IssuerSigningKey = SigningKey
            })
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(AuthenticationSchemes.ApiKey, _ => { });

        services.AddApiAuthorizationPolicies();
        services.AddPermissionAuthorization();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>The claims <c>AuthService.GenerateAccessToken</c> issues, signed the same way.</summary>
    private static string UserToken(string role, SymmetricSecurityKey? signingKey = null)
    {
        var userId = Guid.NewGuid();
        IssuedRoles[userId] = role;
        return Token(signingKey ?? SigningKey,
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, $"{role.ToLowerInvariant()}-test"),
            new Claim(ClaimTypes.Role, role),
            new Claim(ClaimTypes.Email, ""),
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()));
    }

    private static string VanSalesCustomerToken() =>
        Token(SigningKey,
            new Claim(ClaimTypes.Role, ApplicationRoles.VanSalesCustomer),
            new Claim(VanSalesCustomerClaims.AccountId, Guid.NewGuid().ToString()),
            new Claim(VanSalesCustomerClaims.CustomerCode, "SPA059"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()));

    private static string Token(SymmetricSecurityKey signingKey, params Claim[] claims) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)));
}
