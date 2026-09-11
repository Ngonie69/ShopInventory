using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using ShopInventory.Authentication;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Security;
using ShopInventory.Configuration;
using ShopInventory.Controllers;
using ShopInventory.Data;
using ShopInventory.Features.Documents;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A request carrying an API key and a user's bearer token together is that user's request, and the
/// permission checks answer for the user.
/// </summary>
/// <remarks>
/// ShopInventory.Web sends its <c>X-API-Key</c> on every call and the signed-in user's JWT beside it.
/// The API's "ApiAccess" policy authenticates both schemes, so the principal holds the key's identity
/// (role Admin, authentication method ApiKey) and the user's. The permission filter used to ask the
/// whole principal whether it was an Admin API key — which it always was — and skip the check, so no
/// <c>[RequirePermission]</c> restricted any Web user. Every earlier test built a single identity and
/// could not see it.
///
/// These tests do not hand-assemble that principal. They run the real <see cref="ApiKeyAuthenticationHandler"/>
/// and the real JWT bearer handler through ASP.NET Core's own <see cref="PolicyEvaluator"/>, which is
/// what merges the two identities in production, so a change to how either handler shapes its identity
/// shows up here rather than only on a live server.
/// </remarks>
public sealed class ApiKeyWithUserTokenPermissionTests
{
    private const string WebApiKey = "web-app-test-key";
    private const string Issuer = "ShopInventory.Tests";
    private const string Audience = "ShopInventory.Tests.Clients";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("api-key-with-user-token-tests-signing-key-0123456789"));

    /// <summary>
    /// The shape the rest of this class depends on. Without both identities on one principal the
    /// denials below would pass for the wrong reason.
    /// </summary>
    [Fact]
    public async Task A_web_request_authenticates_as_the_key_and_the_user_at_once()
    {
        var cashier = Guid.NewGuid();

        var httpContext = await AuthenticateAsync(
            apiKey: WebApiKey,
            bearerToken: UserToken(cashier, ApplicationRoles.Cashier),
            users: StubProxy.Unused<IUserManagementService>());

        var user = httpContext.User;
        Assert.Equal(2, user.Identities.Count(identity => identity.IsAuthenticated));
        Assert.True(user.IsInRole(ApplicationRoles.Admin), "the key's Admin role is on the merged principal");
        Assert.True(user.IsInRole(ApplicationRoles.Cashier));
        Assert.Equal("ApiKey", user.FindFirst(ClaimTypes.AuthenticationMethod)?.Value);
        Assert.Equal(cashier, UserClaimReader.GetUserId(user));
    }

    /// <summary>
    /// The reported case: a user without <c>creditnotes.add_approved</c>, signed in on the Web, posting a
    /// SAP-held credit memo. The permission is read off the action rather than restated, so the test
    /// follows the endpoint if its guard changes.
    /// </summary>
    [Fact]
    public async Task A_web_user_without_the_permission_is_refused()
    {
        var permissions = AddApprovedCreditNotePermissions();
        var cashier = Guid.NewGuid();
        Assert.DoesNotContain(
            Permission.GetDefaultPermissionsForRole(ApplicationRoles.Cashier),
            permission => permissions.Contains(permission));

        var httpContext = await AuthenticateAsync(
            apiKey: WebApiKey,
            bearerToken: UserToken(cashier, ApplicationRoles.Cashier),
            users: RoleDefaults(cashier, ApplicationRoles.Cashier));

        var context = FilterContext(httpContext);
        await new RequirePermissionAttribute(permissions).OnAuthorizationAsync(context);

        Assert.IsType<ForbidResult>(context.Result);
    }

    [Fact]
    public async Task A_web_user_holding_the_permission_is_let_through()
    {
        var manager = Guid.NewGuid();

        var httpContext = await AuthenticateAsync(
            apiKey: WebApiKey,
            bearerToken: UserToken(manager, ApplicationRoles.Manager),
            users: RoleDefaults(manager, ApplicationRoles.Manager));

        var context = FilterContext(httpContext);
        await new RequirePermissionAttribute(AddApprovedCreditNotePermissions()).OnAuthorizationAsync(context);

        Assert.Null(context.Result);
    }

    /// <summary>
    /// The negative control. An integration with no user — the Web's background jobs, the desktop
    /// integration, the event listener — still passes on its key alone, and nobody's permissions are
    /// looked up for it.
    /// </summary>
    [Fact]
    public async Task An_integration_calling_with_the_key_alone_still_passes()
    {
        var httpContext = await AuthenticateAsync(
            apiKey: WebApiKey,
            bearerToken: null,
            users: StubProxy.Unused<IUserManagementService>());

        var context = FilterContext(httpContext);
        await new RequirePermissionAttribute(AddApprovedCreditNotePermissions()).OnAuthorizationAsync(context);

        Assert.Null(context.Result);
    }

    /// <summary>The handset path, which never sent a key, answers as it always did.</summary>
    [Fact]
    public async Task A_user_token_without_a_key_is_checked_as_before()
    {
        var cashier = Guid.NewGuid();

        var httpContext = await AuthenticateAsync(
            apiKey: null,
            bearerToken: UserToken(cashier, ApplicationRoles.Cashier),
            users: RoleDefaults(cashier, ApplicationRoles.Cashier));

        var context = FilterContext(httpContext);
        await new RequirePermissionAttribute(AddApprovedCreditNotePermissions()).OnAuthorizationAsync(context);

        Assert.IsType<ForbidResult>(context.Result);
    }

    /// <summary>
    /// <see cref="DocumentAttachmentAccessService"/> carried a copy of the same bypass, so a Web user read
    /// any document's attachments as the key's Admin. It now resolves the user and applies their role.
    /// </summary>
    [Fact]
    public async Task A_web_user_is_held_to_their_own_role_for_attachments()
    {
        using var connection = OpenDatabase();
        await using var db = CreateContext(connection);
        var cashier = NewUser(ApplicationRoles.Cashier);
        db.Users.Add(cashier);
        await db.SaveChangesAsync();

        var httpContext = await AuthenticateAsync(
            apiKey: WebApiKey,
            bearerToken: UserToken(cashier.Id, cashier.Role),
            users: StubProxy.Unused<IUserManagementService>());

        var result = await AttachmentAccess(db, httpContext)
            .AuthorizeEntityAccessAsync("SalesOrder", 4471, isWriteOperation: false, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(Errors.Document.AccessDenied(string.Empty).Code, result.FirstError.Code);
    }

    [Fact]
    public async Task An_integration_key_alone_still_reaches_attachments()
    {
        using var connection = OpenDatabase();
        await using var db = CreateContext(connection);

        var httpContext = await AuthenticateAsync(
            apiKey: WebApiKey,
            bearerToken: null,
            users: StubProxy.Unused<IUserManagementService>());

        var result = await AttachmentAccess(db, httpContext)
            .AuthorizeEntityAccessAsync("SalesOrder", 4471, isWriteOperation: false, CancellationToken.None);

        Assert.False(result.IsError);
    }

    /// <summary>
    /// Runs the API's "ApiAccess" policy — built the way Program.cs builds it — through the evaluator the
    /// authorization middleware uses, and returns the request with the principal it produced.
    /// </summary>
    private static async Task<HttpContext> AuthenticateAsync(
        string? apiKey,
        string? bearerToken,
        IUserManagementService users)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(users);
        services.AddSingleton(StubProxy.For<IAuthService>((method, args) =>
            method.Name == nameof(IAuthService.ValidateApiKey) && (string?)args![0] == WebApiKey
                ? new ApiKeyConfig { Key = WebApiKey, Name = "ShopInventory.Web", Roles = [ApplicationRoles.Admin] }
                : throw new InvalidOperationException($"IAuthService.{method.Name} was not expected to be called.")));
        services.AddAuthentication()
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
        services.AddAuthorization();

        var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = provider };

        if (apiKey is not null)
        {
            httpContext.Request.Headers["X-API-Key"] = apiKey;
        }

        if (bearerToken is not null)
        {
            httpContext.Request.Headers.Authorization = $"Bearer {bearerToken}";
        }

        var policy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme, AuthenticationSchemes.ApiKey)
            .RequireRole(ApplicationRoles.ApiAccessRoles)
            .Build();

        var evaluator = new PolicyEvaluator(provider.GetRequiredService<IAuthorizationService>());
        var authentication = await evaluator.AuthenticateAsync(policy, httpContext);
        Assert.True(authentication.Succeeded, "the request should authenticate");

        var authorization = await evaluator.AuthorizeAsync(policy, authentication, httpContext, resource: null);
        Assert.True(authorization.Succeeded, "the request should pass the ApiAccess policy");

        return httpContext;
    }

    /// <summary>The claims <c>AuthService.GenerateAccessToken</c> issues, signed the same way.</summary>
    private static string UserToken(Guid userId, string role)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, $"{role.ToLowerInvariant()}-test"),
            new Claim(ClaimTypes.Role, role),
            new Claim(ClaimTypes.Email, ""),
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string[] AddApprovedCreditNotePermissions()
    {
        var add = typeof(CreditNoteApprovalController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(method => method.GetCustomAttributes<HttpPostAttribute>()
                .Any(attribute => attribute.Template == "{code:int}/add"));

        var permissions = add.GetCustomAttributes<RequirePermissionAttribute>()
            .SelectMany(attribute => attribute.RequiredPermissions)
            .ToArray();

        Assert.Equal([Permission.AddApprovedCreditNotes], permissions);
        return permissions;
    }

    private static IUserManagementService RoleDefaults(Guid userId, string role) =>
        StubProxy.For<IUserManagementService>((method, args) =>
            method.Name == nameof(IUserManagementService.GetEffectivePermissionsAsync) && (Guid)args![0]! == userId
                ? Task.FromResult(Permission.GetDefaultPermissionsForRole(role))
                : throw new InvalidOperationException($"IUserManagementService.{method.Name} was not expected to be called."));

    private static AuthorizationFilterContext FilterContext(HttpContext httpContext) =>
        new(new ActionContext(httpContext, new RouteData(), new ActionDescriptor()), []);

    private static DocumentAttachmentAccessService AttachmentAccess(ApplicationDbContext db, HttpContext httpContext) =>
        new(
            db,
            new HttpContextAccessor { HttpContext = httpContext },
            StubProxy.Unused<IUserManagementService>(),
            StubProxy.Unused<IDocumentService>(),
            NullLogger<DocumentAttachmentAccessService>.Instance);

    private static SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateContext(connection);
        context.Database.EnsureCreated();
        return connection;
    }

    private static ApplicationDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options);

    private static User NewUser(string role) => new()
    {
        Id = Guid.NewGuid(),
        Username = $"{role.ToLowerInvariant()}-test",
        PasswordHash = "not-used",
        Role = role,
        IsActive = true
    };
}
