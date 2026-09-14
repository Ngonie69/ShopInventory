using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.UserManagement.Commands.CreateUser;
using ShopInventory.Models;
using ShopInventory.Services;
using ShopInventory.Web.Data;
using Xunit;
using UserForm = ShopInventory.Web.Components.Pages.UserManagement;

namespace ShopInventory.Tests;

/// <summary>
/// Creating a depot vending cashier — a <see cref="ApplicationRoles.CartVendor"/> account — from the
/// user management form, carrying the business partner, cost centre and warehouse it draws stock from.
///
/// The API has required all three of a cart vendor since the role was added, but the form named the van
/// roles by hand: it showed none of the three for a cart vendor and blanked them on save, so every one
/// it submitted was refused. These tests run the form's own save path — normalise, then validate — and
/// hand the result to the real create handler, which is as far as the page goes before its HTTP call.
/// </summary>
public sealed class CartVendorUserFormTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly Guid _adminId = Guid.NewGuid();

    public CartVendorUserFormTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
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
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private static UserForm.UserFormModel DepotCashier() => new()
    {
        IsNew = true,
        Username = "depot-cashier-01",
        Email = "depot-cashier-01@example.test",
        Password = "a-long-password",
        ConfirmPassword = "a-long-password",
        Role = UserRoles.CartVendor,
        AssignedWarehouseCodes = ["KEFGRC"],
        AssignedBusinessPartnerCode = "VEND-BP",
        AssignedCostCentreCode = "CC-VEND",
    };

    [Fact]
    public void Saving_keeps_the_business_partner_cost_centre_and_warehouse()
    {
        var form = DepotCashier();

        UserForm.NormalizeRoleSpecificAssignments(form);

        Assert.Equal("VEND-BP", form.AssignedBusinessPartnerCode);
        Assert.Equal("CC-VEND", form.AssignedCostCentreCode);
        Assert.Equal(["KEFGRC"], form.AssignedWarehouseCodes);
    }

    [Fact]
    public void Saving_drops_the_van_only_fields()
    {
        // A role switched from Sales to CartVendor in the open form would otherwise still carry the
        // van's depot, route and device, none of which a cart vendor has.
        var form = DepotCashier();
        form.SupplyingWarehouseCode = "KEFBYC";
        form.RouteId = 4;
        form.FiscalDeviceId = 35410;

        UserForm.NormalizeRoleSpecificAssignments(form);

        Assert.Null(form.SupplyingWarehouseCode);
        Assert.Null(form.RouteId);
        Assert.Null(form.FiscalDeviceId);
    }

    [Theory]
    [InlineData(nameof(UserForm.UserFormModel.AssignedBusinessPartnerCode))]
    [InlineData(nameof(UserForm.UserFormModel.AssignedCostCentreCode))]
    [InlineData(nameof(UserForm.UserFormModel.AssignedWarehouseCodes))]
    public void The_form_refuses_a_depot_cashier_missing_an_assignment(string missing)
    {
        var form = DepotCashier();
        switch (missing)
        {
            case nameof(UserForm.UserFormModel.AssignedBusinessPartnerCode): form.AssignedBusinessPartnerCode = null; break;
            case nameof(UserForm.UserFormModel.AssignedCostCentreCode): form.AssignedCostCentreCode = null; break;
            default: form.AssignedWarehouseCodes = []; break;
        }

        var errors = Validate(form);

        Assert.Contains(errors, error => error.MemberNames.Contains(missing));
    }

    [Fact]
    public void The_form_does_not_ask_a_depot_cashier_for_a_supplying_warehouse()
    {
        Assert.Empty(Validate(DepotCashier()));
    }

    [Fact]
    public async Task A_depot_cashier_submitted_from_the_form_is_created_and_can_sell()
    {
        var form = DepotCashier();
        UserForm.NormalizeRoleSpecificAssignments(form);
        Assert.Empty(Validate(form));

        // The fields UserManagementService.CreateUserAsync posts, taken from the form as it posts them.
        var result = await new CreateUserHandler(
                _context,
                OfficeContext(),
                new NoOpAuditService(),
                NullLogger<CreateUserHandler>.Instance)
            .Handle(
                new CreateUserCommand(new CreateUserDetailRequest
                {
                    Username = form.Username,
                    Email = form.Email,
                    Password = form.Password!,
                    Role = form.Role,
                    AssignedWarehouseCodes = form.AssignedWarehouseCodes,
                    AssignedBusinessPartnerCode = form.AssignedBusinessPartnerCode,
                    AssignedCostCentreCode = form.AssignedCostCentreCode,
                    SupplyingWarehouseCode = form.SupplyingWarehouseCode,
                    RouteId = form.RouteId,
                    FiscalDeviceId = form.FiscalDeviceId,
                    ShopId = form.ShopId
                }),
                CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);

        var saved = await _context.Users.AsNoTracking().SingleAsync(user => user.Username == form.Username);
        var identity = SellingAccountResolver.Resolve(saved);

        Assert.False(identity.IsError, identity.IsError ? identity.FirstError.Description : null);
        Assert.Equal("VEND-BP", identity.Value.CardCode);
        Assert.Equal("KEFGRC", identity.Value.WarehouseCode);
        Assert.Equal("CC-VEND", identity.Value.CostCentreCode);
    }

    private static List<ValidationResult> Validate(UserForm.UserFormModel form)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(form, new ValidationContext(form), results, validateAllProperties: true);
        return results;
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
