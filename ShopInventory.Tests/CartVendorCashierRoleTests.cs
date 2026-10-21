using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Auth;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Documents;
using ShopInventory.Features.UserManagement.Commands.CreateUser;
using ShopInventory.Features.UserManagement.Commands.UpdateUser;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Web.Data;
using UserForm = ShopInventory.Web.Components.Pages.UserManagement;

namespace ShopInventory.Tests;

/// <summary>
/// One account that is both a cart vendor and a cashier: CartVendor stays the main role, so the till
/// keeps selling to vendors, and Cashier is held as an extra role, so the Web and API open the cashier
/// pages and permissions too.
/// </summary>
public sealed class CartVendorCashierRoleTests : IDisposable
{
    private const string Ip = "10.10.11.27";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly Guid _adminId = Guid.NewGuid();

    public CartVendorCashierRoleTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = _adminId,
            Username = "office",
            Email = "office@example.test",
            PasswordHash = "x",
            Role = ApplicationRoles.Admin,
            IsActive = true
        });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---- Which pairs are allowed -----------------------------------------------------------------

    [Fact]
    public void A_cart_vendor_may_also_be_a_cashier()
    {
        var (roles, error) = ApplicationRoles.NormalizeAdditionalRoles(ApplicationRoles.CartVendor, ["cashier", "Cashier "]);

        Assert.Null(error);
        Assert.Equal([ApplicationRoles.Cashier], roles);
    }

    [Theory]
    [InlineData(ApplicationRoles.CartVendor, ApplicationRoles.Admin)]
    [InlineData(ApplicationRoles.CartVendor, ApplicationRoles.Manager)]
    [InlineData(ApplicationRoles.Cashier, ApplicationRoles.CartVendor)]
    [InlineData(ApplicationRoles.Merchandiser, ApplicationRoles.Cashier)]
    public void Other_pairs_are_refused(string mainRole, string extraRole)
    {
        var (_, error) = ApplicationRoles.NormalizeAdditionalRoles(mainRole, [extraRole]);

        Assert.NotNull(error);
    }

    [Fact]
    public void Naming_the_main_role_again_adds_nothing()
    {
        var (roles, error) = ApplicationRoles.NormalizeAdditionalRoles(ApplicationRoles.CartVendor, [ApplicationRoles.CartVendor]);

        Assert.Null(error);
        Assert.Empty(roles);
    }

    /// <summary>The Web form offers exactly the extra roles the API accepts, for every role.</summary>
    [Fact]
    public void The_web_offers_the_same_extra_roles_as_the_api()
    {
        foreach (var role in ApplicationRoles.RetainableManagedRoles)
        {
            Assert.Equal(ApplicationRoles.AllowedAdditionalRoles(role), UserRoles.AllowedAdditionalRoles(role));
        }
    }

    // ---- Creating the account --------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_cart_vendor_who_is_also_a_cashier_saves_both()
    {
        var result = await CreateAsync([ApplicationRoles.Cashier]);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal(ApplicationRoles.CartVendor, result.Value.Role);
        Assert.Equal([ApplicationRoles.Cashier], result.Value.AdditionalRoles);

        var saved = await _context.Users.AsNoTracking().SingleAsync(user => user.Id == result.Value.Id);
        Assert.Equal(ApplicationRoles.CartVendor, saved.Role);
        Assert.Equal([ApplicationRoles.Cashier], saved.GetAdditionalRoles());
    }

    [Fact]
    public async Task Creating_a_cart_vendor_with_a_role_it_may_not_hold_is_refused()
    {
        var result = await CreateAsync([ApplicationRoles.Admin]);

        Assert.True(result.IsError);
        Assert.Empty(await _context.Users.Where(user => user.Username == "depot-cashier-01").ToListAsync());
    }

    // ---- What the account gets -------------------------------------------------------------------

    /// <summary>
    /// The till picks its mode from the role it is told at sign-in, so that must stay CartVendor.
    /// The token carries both, main role first.
    /// </summary>
    [Fact]
    public async Task The_till_still_signs_in_as_a_cart_vendor_and_the_token_carries_both_roles()
    {
        var user = await CreatedUserAsync([ApplicationRoles.Cashier]);

        var info = await UserInfoMapper.FromUserAsync(user, _context);
        Assert.Equal(ApplicationRoles.CartVendor, info.Role);
        Assert.Equal([ApplicationRoles.Cashier], info.AdditionalRoles);

        var roleClaims = await RoleClaimsAsync(user);
        Assert.Equal([ApplicationRoles.CartVendor, ApplicationRoles.Cashier], roleClaims);
    }

    [Fact]
    public async Task A_plain_cart_vendors_token_carries_one_role()
    {
        var user = await CreatedUserAsync([]);

        Assert.Equal([ApplicationRoles.CartVendor], await RoleClaimsAsync(user));
    }

    [Fact]
    public async Task The_cashier_permissions_are_added_to_the_cart_vendors()
    {
        var plain = await CreatedUserAsync([], "plain-vendor");
        var both = await CreatedUserAsync([ApplicationRoles.Cashier], "vendor-and-cashier");
        var service = new UserManagementService(_context, new MemoryCache(new MemoryCacheOptions()), NullLogger<UserManagementService>.Instance);

        var plainPermissions = await service.GetEffectivePermissionsAsync(plain.Id);
        var bothPermissions = await service.GetEffectivePermissionsAsync(both.Id);

        Assert.DoesNotContain(Permission.CreatePayments, plainPermissions);
        Assert.DoesNotContain(Permission.CreateSalesOrders, plainPermissions);

        foreach (var permission in Permission.GetDefaultPermissionsForRole(ApplicationRoles.Cashier)
                     .Concat(Permission.GetDefaultPermissionsForRole(ApplicationRoles.CartVendor)))
        {
            Assert.Contains(permission, bothPermissions);
        }
    }

    /// <summary>
    /// Invoice PODs are open to a Cashier and not to a CartVendor. The service reads the role from the
    /// account rather than the token, so it has to look at the extra role too.
    /// </summary>
    [Fact]
    public async Task The_cashier_role_opens_invoice_pods()
    {
        var plain = await CreatedUserAsync([], "plain-vendor");
        var both = await CreatedUserAsync([ApplicationRoles.Cashier], "vendor-and-cashier");

        var plainResult = await AttachmentAccess(plain).AuthorizeEntityAccessAsync("Invoice", 2392456, false, CancellationToken.None);
        var bothResult = await AttachmentAccess(both).AuthorizeEntityAccessAsync("Invoice", 2392456, false, CancellationToken.None);

        Assert.True(plainResult.IsError);
        Assert.False(bothResult.IsError, bothResult.IsError ? bothResult.FirstError.Description : null);
    }

    // ---- Editing the account ---------------------------------------------------------------------

    [Fact]
    public async Task Cashier_can_be_added_to_an_existing_cart_vendor_and_removed_again()
    {
        var user = await CreatedUserAsync([]);

        var added = await UpdateAsync(user.Id, CartVendorForm([ApplicationRoles.Cashier]));
        Assert.False(added.IsError, added.IsError ? added.FirstError.Description : null);
        Assert.Equal([ApplicationRoles.Cashier], (await ReloadAsync(user.Id)).GetAdditionalRoles());

        // Leaving the field out (null) keeps what the account has.
        var untouched = await UpdateAsync(user.Id, CartVendorForm(null));
        Assert.False(untouched.IsError, untouched.IsError ? untouched.FirstError.Description : null);
        Assert.Equal([ApplicationRoles.Cashier], (await ReloadAsync(user.Id)).GetAdditionalRoles());

        var removed = await UpdateAsync(user.Id, CartVendorForm([]));
        Assert.False(removed.IsError, removed.IsError ? removed.FirstError.Description : null);
        var reloaded = await ReloadAsync(user.Id);
        Assert.Empty(reloaded.GetAdditionalRoles());
        Assert.Null(reloaded.AdditionalRoles);
    }

    [Fact]
    public async Task Moving_the_account_off_cart_vendor_drops_the_extra_role()
    {
        var user = await CreatedUserAsync([ApplicationRoles.Cashier]);

        var result = await UpdateAsync(user.Id, new UpdateUserDetailRequest { Role = ApplicationRoles.Manager });

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        var reloaded = await ReloadAsync(user.Id);
        Assert.Equal(ApplicationRoles.Manager, reloaded.Role);
        Assert.Empty(reloaded.GetAdditionalRoles());
    }

    [Fact]
    public async Task Adding_a_role_the_cart_vendor_may_not_hold_is_refused()
    {
        var user = await CreatedUserAsync([]);

        var result = await UpdateAsync(user.Id, CartVendorForm([ApplicationRoles.Admin]));

        Assert.True(result.IsError);
        Assert.Empty((await ReloadAsync(user.Id)).GetAdditionalRoles());
    }

    // ---- The Web form ----------------------------------------------------------------------------

    [Fact]
    public void The_form_keeps_cashier_on_a_cart_vendor_and_drops_it_on_any_other_role()
    {
        var form = new UserForm.UserFormModel
        {
            Role = UserRoles.CartVendor,
            AdditionalRoles = [UserRoles.Cashier],
            AssignedWarehouseCodes = ["KEFGRC"],
            AssignedBusinessPartnerCode = "VEND-BP",
            AssignedCostCentreCode = "CC-VEND"
        };

        UserForm.NormalizeRoleSpecificAssignments(form);
        Assert.Equal([UserRoles.Cashier], form.AdditionalRoles);

        form.Role = UserRoles.Manager;
        UserForm.NormalizeRoleSpecificAssignments(form);
        Assert.Empty(form.AdditionalRoles);
    }

    [Fact]
    public void Signing_in_on_the_web_lands_a_vendor_who_is_also_a_cashier_on_the_cashier_dashboard()
    {
        Assert.Equal(
            ShopInventory.Web.Common.RoleLandingRoutes.CashierDashboard,
            ShopInventory.Web.Common.RoleLandingRoutes.For(UserRoles.CartVendor, [UserRoles.Cashier]));

        Assert.NotEqual(
            ShopInventory.Web.Common.RoleLandingRoutes.CashierDashboard,
            ShopInventory.Web.Common.RoleLandingRoutes.For(UserRoles.CartVendor));
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private Task<ErrorOr.ErrorOr<UserDetailDto>> CreateAsync(List<string> additionalRoles, string username = "depot-cashier-01") =>
        new CreateUserHandler(
                _context,
                OfficeContext(),
                new NoOpAuditService(),
                TestPasswords.For(_context),
                NullLogger<CreateUserHandler>.Instance)
            .Handle(
                new CreateUserCommand(new CreateUserDetailRequest
                {
                    Username = username,
                    Email = $"{username}@example.test",
                    Password = "a-long-Passw0rd!",
                    Role = ApplicationRoles.CartVendor,
                    AdditionalRoles = additionalRoles,
                    AssignedWarehouseCodes = ["KEFGRC"],
                    AssignedBusinessPartnerCode = "VEND-BP",
                    AssignedCostCentreCode = "CC-VEND"
                }),
                CancellationToken.None);

    private async Task<User> CreatedUserAsync(List<string> additionalRoles, string username = "depot-cashier-01")
    {
        var result = await CreateAsync(additionalRoles, username);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        _context.ChangeTracker.Clear();
        return await ReloadAsync(result.Value.Id);
    }

    /// <summary>The whole cart vendor form, as the Web sends it on every save.</summary>
    private static UpdateUserDetailRequest CartVendorForm(List<string>? additionalRoles) => new()
    {
        Role = ApplicationRoles.CartVendor,
        AdditionalRoles = additionalRoles,
        AssignedWarehouseCodes = ["KEFGRC"],
        AssignedBusinessPartnerCode = "VEND-BP",
        AssignedCostCentreCode = "CC-VEND"
    };

    private Task<User> ReloadAsync(Guid id) => _context.Users.AsNoTracking().SingleAsync(user => user.Id == id);

    private Task<ErrorOr.ErrorOr<ErrorOr.Success>> UpdateAsync(Guid id, UpdateUserDetailRequest request)
    {
        _context.ChangeTracker.Clear();
        return new UpdateUserHandler(
                _context,
                OfficeContext(),
                new MemoryCache(new MemoryCacheOptions()),
                new NoOpAuditService(),
                StubProxy.Unused<IBusinessPartnerService>(),
                StubProxy.Unused<INotificationService>(),
                NullLogger<UpdateUserHandler>.Instance)
            .Handle(new UpdateUserCommand(id, request), CancellationToken.None);
    }

    /// <summary>Issues a token through a refresh and reads its role claims back, in order.</summary>
    private async Task<List<string>> RoleClaimsAsync(User user)
    {
        var refreshValue = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        _context.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(refreshValue))),
            UserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedByIp = Ip
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var service = new AuthService(
            _context,
            Options.Create(new JwtSettings
            {
                SecretKey = new string('k', 64),
                Issuer = "ShopInventoryAPI",
                Audience = "ShopInventoryClients",
                AccessTokenExpirationMinutes = 60,
                RefreshTokenExpirationDays = 7
            }),
            Options.Create(new SecuritySettings()),
            new CapturingLogger<AuthService>(),
            StubProxy.Unused<ITwoFactorPendingStore>(),
            StubProxy.Unused<ITwoFactorService>(),
            TestPasswords.For(_context));

        var response = await service.RefreshTokenAsync(refreshValue, Ip);
        Assert.NotNull(response);

        // The handler maps "role" back to ClaimTypes.Role on the way in; read it raw here.
        return new JwtSecurityTokenHandler()
            .ReadJwtToken(response!.AccessToken)
            .Claims
            .Where(claim => claim.Type is "role" or ClaimTypes.Role)
            .Select(claim => claim.Value)
            .ToList();
    }

    private DocumentAttachmentAccessService AttachmentAccess(User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username)
        };
        claims.AddRange(user.GetAllRoles().Select(role => new Claim(ClaimTypes.Role, role)));

        return new DocumentAttachmentAccessService(
            _context,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
            StubProxy.Unused<IUserManagementService>(),
            StubProxy.Unused<IDocumentService>(),
            NullLogger<DocumentAttachmentAccessService>.Instance);
    }

    private IHttpContextAccessor OfficeContext() =>
        new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.Name, "office"),
                        new Claim(ClaimTypes.NameIdentifier, _adminId.ToString())
                    ],
                    "Test"))
            }
        };

    private sealed class NoOpAuditService : IAuditService
    {
        public Task LogAsync(string action, string username, string userRole, string? entityType = null,
            string? entityId = null, string? details = null, string? endpoint = null,
            bool isSuccess = true, string? errorMessage = null) => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType = null, string? entityId = null) => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType, string? entityId, string? details,
            bool isSuccess, string? errorMessage = null) => Task.CompletedTask;
    }
}
