using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.UserManagement.Commands.CreateUser;
using ShopInventory.Features.UserManagement.Commands.UpdateUser;
using ShopInventory.Models;
using ShopInventory.Services;
using UserForm = ShopInventory.Web.Components.Pages.UserManagement;

namespace ShopInventory.Tests;

/// <summary>
/// Setting the business partner whose sales a ShopStock account reads, from the user screen.
///
/// The update writes an explicit column list (<c>ExecuteUpdate</c>), so a field can be accepted and still
/// never reach the row; each assertion reads the row back past the change tracker. The other half is that
/// the grant belongs to ShopStock alone: no other role keeps one, and a user moved off ShopStock loses it.
/// </summary>
public sealed class ShopStockSalesPartnerUserTests : IDisposable
{
    private const string Partner = "FARM-BP";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly Guid _adminId = Guid.NewGuid();

    public ShopStockSalesPartnerUserTests()
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
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_new_stock_user_keeps_its_sales_partner()
    {
        var created = await CreateAsync(ApplicationRoles.ShopStock, " " + Partner + " ");

        Assert.False(created.IsError);
        Assert.Equal(Partner, created.Value.SalesBusinessPartnerCode);
        Assert.Equal(Partner, await ReadPartnerAsync(created.Value.Id));
    }

    [Fact]
    public async Task A_new_stock_user_may_have_no_sales_partner()
    {
        var created = await CreateAsync(ApplicationRoles.ShopStock, null);

        Assert.False(created.IsError);
        Assert.Null(await ReadPartnerAsync(created.Value.Id));
    }

    [Fact]
    public async Task Another_role_is_not_given_a_sales_partner()
    {
        var created = await CreateAsync(ApplicationRoles.StockController, Partner);

        Assert.False(created.IsError);
        Assert.Null(await ReadPartnerAsync(created.Value.Id));
    }

    [Fact]
    public async Task Setting_the_partner_on_an_existing_stock_user_reaches_the_row()
    {
        var user = await SeedStockUserAsync(partner: null);

        var result = await UpdateAsync(user.Id, Request(ApplicationRoles.ShopStock, Partner));

        Assert.False(result.IsError);
        Assert.Equal(Partner, await ReadPartnerAsync(user.Id));
    }

    [Fact]
    public async Task Clearing_the_partner_reaches_the_row()
    {
        var user = await SeedStockUserAsync(partner: Partner);

        var result = await UpdateAsync(user.Id, Request(ApplicationRoles.ShopStock, null));

        Assert.False(result.IsError);
        Assert.Null(await ReadPartnerAsync(user.Id));
    }

    [Fact]
    public async Task A_user_moved_off_ShopStock_loses_the_partner()
    {
        var user = await SeedStockUserAsync(partner: Partner);

        var result = await UpdateAsync(user.Id, Request(ApplicationRoles.StockController, Partner));

        Assert.False(result.IsError);
        Assert.Null(await ReadPartnerAsync(user.Id));
    }

    [Fact]
    public async Task The_sales_partner_is_not_written_into_the_selling_partner()
    {
        var created = await CreateAsync(ApplicationRoles.ShopStock, Partner);

        var row = await _context.Users.AsNoTracking().SingleAsync(user => user.Id == created.Value.Id);
        Assert.Null(row.AssignedBusinessPartnerCode);
    }

    [Fact]
    public void The_form_drops_the_partner_when_the_role_changes_away_from_ShopStock()
    {
        var form = new UserForm.UserFormModel
        {
            Role = "StockController",
            AssignedWarehouseCodes = ["KEFSHOP"],
            SalesBusinessPartnerCode = Partner
        };

        UserForm.NormalizeRoleSpecificAssignments(form);

        Assert.Null(form.SalesBusinessPartnerCode);
    }

    [Fact]
    public void The_form_keeps_the_partner_for_ShopStock()
    {
        var form = new UserForm.UserFormModel
        {
            Role = "ShopStock",
            AssignedWarehouseCodes = ["KEFSHOP"],
            SalesBusinessPartnerCode = Partner
        };

        UserForm.NormalizeRoleSpecificAssignments(form);

        Assert.Equal(Partner, form.SalesBusinessPartnerCode);
    }

    // ---- Harness ----------------------------------------------------------------------------------

    private static UpdateUserDetailRequest Request(string role, string? partner) => new()
    {
        Role = role,
        AssignedWarehouseCodes = ["KEFSHOP"],
        SalesBusinessPartnerCode = partner
    };

    private async Task<User> SeedStockUserAsync(string? partner)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"stock-{Guid.NewGuid():N}"[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            PasswordHash = "x",
            Role = ApplicationRoles.ShopStock,
            FirstName = "Shop",
            LastName = "Stock",
            IsActive = true,
            SalesBusinessPartnerCode = partner
        };
        user.SetWarehouseCodes(["KEFSHOP"]);
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return user;
    }

    private async Task<string?> ReadPartnerAsync(Guid userId) =>
        await _context.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.SalesBusinessPartnerCode)
            .SingleAsync();

    private Task<ErrorOr.ErrorOr<ErrorOr.Success>> UpdateAsync(Guid userId, UpdateUserDetailRequest request) =>
        new UpdateUserHandler(
                _context,
                OfficeContext(),
                new MemoryCache(new MemoryCacheOptions()),
                new NoOpAuditService(),
                StubProxy.Unused<IBusinessPartnerService>(),
                StubProxy.Unused<INotificationService>(),
                NullLogger<UpdateUserHandler>.Instance)
            .Handle(new UpdateUserCommand(userId, request), CancellationToken.None);

    private Task<ErrorOr.ErrorOr<UserDetailDto>> CreateAsync(string role, string? partner) =>
        new CreateUserHandler(
                _context,
                OfficeContext(),
                new NoOpAuditService(),
                TestPasswords.For(_context),
                NullLogger<CreateUserHandler>.Instance)
            .Handle(
                new CreateUserCommand(new CreateUserDetailRequest
                {
                    Username = $"stock-{Guid.NewGuid():N}"[..20],
                    Email = $"{Guid.NewGuid():N}@example.test",
                    Password = "not-a-real-password",
                    Role = role,
                    AssignedWarehouseCodes = ["KEFSHOP"],
                    SalesBusinessPartnerCode = partner
                }),
                CancellationToken.None);

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
