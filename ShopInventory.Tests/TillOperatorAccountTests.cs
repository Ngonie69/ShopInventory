using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Auth;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.UserManagement.Commands.CreateUser;
using ShopInventory.Features.UserManagement.Commands.UpdateUser;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Opening and editing a till operator account.
///
/// The rule these pin: a till operator's business partner, warehouse and cost centre come from its
/// shop and from nowhere else. An account carrying both a shop and the three loose codes has two
/// sources for one answer, and <c>SellingAccountResolver</c> reads only the shop — so an administrator
/// who typed the codes would believe the till sells on values it will silently ignore. The refusal is
/// what makes the single source real rather than merely conventional.
/// </summary>
public sealed class TillOperatorAccountTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly Guid _adminId = Guid.NewGuid();

    private int _shopId;
    private int _closedShopId;

    public TillOperatorAccountTests()
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

        var open = new ShopEntity
        {
            Code = "MACHIPISA",
            Name = "Machipisa",
            BusinessPartnerCode = "MACH-BP",
            WarehouseCode = "CORMACH2",
            CostCentreCode = "CC-MACH",
            IsActive = true,
        };
        var closed = new ShopEntity
        {
            Code = "OLDTOWN",
            Name = "Old Town",
            BusinessPartnerCode = "OLD-BP",
            WarehouseCode = "KEFOLD",
            IsActive = false,
        };

        _context.Shops.AddRange(open, closed);
        _context.SaveChanges();

        _shopId = open.Id;
        _closedShopId = closed.Id;
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---- Creating -------------------------------------------------------------------------------

    [Fact]
    public async Task A_till_operator_opens_against_a_shop()
    {
        var result = await CreateAsync(Request(shopId: _shopId));

        Assert.False(result.IsError);

        var created = await ReadAsync(result.Value.Id);
        Assert.Equal(_shopId, created.ShopId);
    }

    [Fact]
    public async Task The_three_loose_codes_are_left_null_on_a_till_operator()
    {
        // The shop is where they come from. Copying them onto the account would give one value two
        // homes that drift apart the moment the shop is edited.
        var result = await CreateAsync(Request(shopId: _shopId));

        Assert.False(result.IsError);

        var created = await ReadAsync(result.Value.Id);
        Assert.Null(created.AssignedBusinessPartnerCode);
        Assert.Null(created.AssignedCostCentreCode);
        Assert.Null(created.SupplyingWarehouseCode);
        Assert.Empty(created.GetWarehouseCodes());
    }

    [Fact]
    public async Task A_till_operator_without_a_shop_is_refused()
    {
        var result = await CreateAsync(Request(shopId: null));

        Assert.True(result.IsError);
        Assert.Contains("shop is required", result.FirstError.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_till_operator_naming_a_shop_that_does_not_exist_is_refused()
    {
        var result = await CreateAsync(Request(shopId: 4242));

        Assert.True(result.IsError);
        Assert.Contains("not found", result.FirstError.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_till_operator_cannot_be_opened_on_a_closed_shop()
    {
        var result = await CreateAsync(Request(shopId: _closedShopId));

        Assert.True(result.IsError);
        Assert.Contains("closed", result.FirstError.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("businessPartner")]
    [InlineData("costCentre")]
    [InlineData("supplyingWarehouse")]
    [InlineData("warehouseCodes")]
    public async Task A_till_operator_carrying_a_shop_and_a_loose_code_is_refused(string field)
    {
        // Both set is a configuration error, not a preference. Accepting it would let an administrator
        // believe the code they typed is the one the till will sell on.
        var request = Request(shopId: _shopId);

        switch (field)
        {
            case "businessPartner": request.AssignedBusinessPartnerCode = "OTHER-BP"; break;
            case "costCentre": request.AssignedCostCentreCode = "CC-OTHER"; break;
            case "supplyingWarehouse": request.SupplyingWarehouseCode = "KEFGRC"; break;
            case "warehouseCodes": request.AssignedWarehouseCodes = ["KEFGRS"]; break;
        }

        var result = await CreateAsync(request);

        Assert.True(result.IsError);
        Assert.Contains("from its shop", result.FirstError.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_shop_cannot_be_assigned_to_a_role_that_is_not_a_till_operator()
    {
        var request = Request(shopId: _shopId);
        request.Role = ApplicationRoles.Cashier;

        var result = await CreateAsync(request);

        Assert.True(result.IsError);
        Assert.Contains("can only be assigned", result.FirstError.Description, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Editing --------------------------------------------------------------------------------

    [Fact]
    public async Task A_till_operator_can_be_moved_to_another_shop()
    {
        var second = new ShopEntity
        {
            Code = "FARM",
            Name = "Farm",
            BusinessPartnerCode = "FARM-BP",
            WarehouseCode = "KEFSHOP",
            IsActive = true,
        };
        _context.Shops.Add(second);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var created = await CreateAsync(Request(shopId: _shopId));
        var result = await UpdateAsync(created.Value.Id, UpdateRequest(shopId: second.Id));

        Assert.False(result.IsError);
        Assert.Equal(second.Id, (await ReadAsync(created.Value.Id)).ShopId);
    }

    [Fact]
    public async Task A_till_operator_cannot_be_moved_to_a_closed_shop()
    {
        // Checked on edit as well as on create, or a closed shop would be reachable by moving an
        // existing account onto it.
        var created = await CreateAsync(Request(shopId: _shopId));

        var result = await UpdateAsync(created.Value.Id, UpdateRequest(shopId: _closedShopId));

        Assert.True(result.IsError);
        Assert.Contains("closed", result.FirstError.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clearing_a_till_operators_shop_is_refused()
    {
        var created = await CreateAsync(Request(shopId: _shopId));

        var result = await UpdateAsync(created.Value.Id, UpdateRequest(shopId: null));

        Assert.True(result.IsError);
        Assert.Contains("shop is required", result.FirstError.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Moving_an_account_off_the_till_role_releases_its_shop()
    {
        // Leaving it set would keep them scoped to that shop's sales for reading, which is not what a
        // promotion should mean.
        var created = await CreateAsync(Request(shopId: _shopId));

        var promotion = UpdateRequest(shopId: _shopId);
        promotion.Role = ApplicationRoles.Manager;

        var result = await UpdateAsync(created.Value.Id, promotion);

        Assert.False(result.IsError);
        Assert.Null((await ReadAsync(created.Value.Id)).ShopId);
    }

    // ---- What the client is told about itself ---------------------------------------------------

    [Fact]
    public async Task Sign_in_reports_the_shops_codes_in_the_fields_a_client_already_reads()
    {
        // So a till needs no knowledge of shops to get the right answer, and cannot end up showing one
        // warehouse while selling from another.
        var created = await CreateAsync(Request(shopId: _shopId));
        var user = await ReadAsync(created.Value.Id);

        var info = await UserInfoMapper.FromUserAsync(user, _context);

        Assert.Equal("MACH-BP", info.AssignedBusinessPartnerCode);
        Assert.Equal("CC-MACH", info.AssignedCostCentreCode);
        Assert.Equal("CORMACH2", info.AssignedWarehouseCode);
        Assert.Equal("CORMACH2", Assert.Single(info.AssignedWarehouseCodes));
        Assert.Equal("MACHIPISA", info.ShopCode);
        Assert.Equal("Machipisa", info.ShopName);
    }

    [Fact]
    public async Task Sign_in_for_an_account_with_no_shop_reports_its_own_columns()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "van001",
            PasswordHash = "x",
            Role = ApplicationRoles.Sales,
            IsActive = true,
            AssignedBusinessPartnerCode = "VAN-BP",
            AssignedCostCentreCode = "CC-VAN",
        };
        user.AssignedWarehouseCode = "VAN001";
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var info = await UserInfoMapper.FromUserAsync(user, _context);

        Assert.Equal("VAN-BP", info.AssignedBusinessPartnerCode);
        Assert.Equal("VAN001", info.AssignedWarehouseCode);
        Assert.Null(info.ShopCode);
        Assert.Null(info.ShopName);
    }

    [Fact]
    public async Task Sign_in_resolves_the_shop_even_when_the_caller_did_not_load_it()
    {
        // The five paths that build a login payload reach their user differently and two of them use
        // FindAsync, which cannot Include. The mapper resolving it is what stops a till being handed
        // a payload with no shop on it.
        var created = await CreateAsync(Request(shopId: _shopId));

        var unloaded = await _context.Users
            .AsNoTracking()
            .FirstAsync(user => user.Id == created.Value.Id);

        Assert.Null(unloaded.Shop);

        var info = await UserInfoMapper.FromUserAsync(unloaded, _context);

        Assert.Equal("MACHIPISA", info.ShopCode);
        Assert.Equal("CORMACH2", info.AssignedWarehouseCode);
    }

    // ---- Harness --------------------------------------------------------------------------------

    private static CreateUserDetailRequest Request(int? shopId) => new()
    {
        Username = $"till-{Guid.NewGuid():N}",
        Email = $"{Guid.NewGuid():N}@example.test",
        Password = "not-a-real-password",
        Role = ApplicationRoles.TillOperator,
        ShopId = shopId,
    };

    private static UpdateUserDetailRequest UpdateRequest(int? shopId) => new()
    {
        Role = ApplicationRoles.TillOperator,
        IsActive = true,
        ShopId = shopId,
    };

    private Task<ErrorOr.ErrorOr<UserDetailDto>> CreateAsync(CreateUserDetailRequest request)
    {
        var handler = new CreateUserHandler(
            _context,
            OfficeContext(),
            new NoOpAuditService(),
            NullLogger<CreateUserHandler>.Instance);

        return handler.Handle(new CreateUserCommand(request), CancellationToken.None);
    }

    private Task<ErrorOr.ErrorOr<ErrorOr.Success>> UpdateAsync(Guid userId, UpdateUserDetailRequest request)
    {
        var handler = new UpdateUserHandler(
            _context,
            OfficeContext(),
            new MemoryCache(new MemoryCacheOptions()),
            new NoOpAuditService(),
            StubProxy.Unused<IBusinessPartnerService>(),
            StubProxy.Unused<INotificationService>(),
            NullLogger<UpdateUserHandler>.Instance);

        return handler.Handle(new UpdateUserCommand(userId, request), CancellationToken.None);
    }

    /// <summary>The row as it now stands, read past the change tracker rather than from it.</summary>
    private async Task<User> ReadAsync(Guid userId)
    {
        _context.ChangeTracker.Clear();
        return await _context.Users.AsNoTracking().SingleAsync(user => user.Id == userId);
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
