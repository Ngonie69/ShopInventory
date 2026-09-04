using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Shops.Commands.CreateShop;
using ShopInventory.Features.Shops.Commands.SetShopActive;
using ShopInventory.Features.Shops.Commands.UpdateShop;
using ShopInventory.Features.Shops.Queries.GetShopById;
using ShopInventory.Features.Shops.Queries.GetShops;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the rules that keep a shop's selling identity usable.
///
/// A shop is the one place a till operator's business partner, warehouse and cost centre come from,
/// and its warehouse is also what scopes which sales its operators may read. Both of those make the
/// warehouse-uniqueness rule and the close-with-operators rule load-bearing rather than tidiness.
/// </summary>
public sealed class ShopManagementTests : IDisposable
{
    private static readonly Guid AdminId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public ShopManagementTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        // CreatedByUserId/UpdatedByUserId are real foreign keys, so the administrator has to exist
        // before a shop can name them. SQLite enforces that; the EF in-memory provider would not.
        _context.Users.Add(new User
        {
            Id = AdminId,
            Username = "admin",
            PasswordHash = "not-used-here",
            Role = ApplicationRoles.Admin,
            IsActive = true,
        });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---- Opening a shop -------------------------------------------------------------------------

    [Fact]
    public async Task A_shop_opens_with_the_values_it_was_given()
    {
        var result = await Create(Request());

        Assert.False(result.IsError);
        Assert.Equal("MACHIPISA", result.Value.Code);
        Assert.Equal("Machipisa", result.Value.Name);
        Assert.Equal("SHOP-BP", result.Value.BusinessPartnerCode);
        Assert.Equal("CORMACH2", result.Value.WarehouseCode);
        Assert.Equal("CC-SHOP", result.Value.CostCentreCode);
        Assert.True(result.Value.IsActive);
        Assert.Equal(0, result.Value.AssignedOperatorCount);
    }

    [Fact]
    public async Task Codes_and_names_are_trimmed_on_the_way_in()
    {
        var result = await Create(Request(code: "  MACHIPISA  ", warehouse: "  CORMACH2 "));

        Assert.False(result.IsError);
        Assert.Equal("MACHIPISA", result.Value.Code);
        Assert.Equal("CORMACH2", result.Value.WarehouseCode);
    }

    [Fact]
    public async Task A_blank_cost_centre_is_stored_as_absent_rather_than_as_whitespace()
    {
        // SellingAccountResolver treats whitespace as absent, so storing "   " here would put a value
        // on the row that never reaches a sale — a difference visible only on the admin screen.
        var result = await Create(Request(costCentre: "   "));

        Assert.False(result.IsError);
        Assert.Null(result.Value.CostCentreCode);
    }

    [Fact]
    public async Task A_duplicate_code_is_refused()
    {
        await Create(Request());

        var result = await Create(Request(warehouse: "KEFGRS"));

        Assert.True(result.IsError);
        Assert.Equal("Shops.DuplicateCode", result.FirstError.Code);
    }

    [Fact]
    public async Task A_duplicate_code_differing_only_in_case_is_refused()
    {
        await Create(Request());

        var result = await Create(Request(code: "machipisa", warehouse: "KEFGRS"));

        Assert.True(result.IsError);
        Assert.Equal("Shops.DuplicateCode", result.FirstError.Code);
    }

    [Fact]
    public async Task Two_shops_cannot_share_a_warehouse()
    {
        // The warehouse is what scopes a till operator's view of the day's takings, so two shops on
        // one warehouse would show each other's sales to both.
        await Create(Request());

        var result = await Create(Request(code: "FARM", name: "Farm"));

        Assert.True(result.IsError);
        Assert.Equal("Shops.WarehouseAlreadyAssigned", result.FirstError.Code);
        Assert.Contains("Machipisa", result.FirstError.Description);
    }

    [Fact]
    public async Task A_closed_shops_warehouse_is_still_reserved()
    {
        // A closed shop keeps its sales history, and that history is scoped by warehouse — so handing
        // the warehouse on would show the old shop's takings to the new shop's operators.
        var opened = await Create(Request());
        await SetActive(opened.Value.Id, isActive: false);

        var result = await Create(Request(code: "FARM", name: "Farm"));

        Assert.True(result.IsError);
        Assert.Equal("Shops.WarehouseAlreadyAssigned", result.FirstError.Code);
    }

    // ---- Editing --------------------------------------------------------------------------------

    [Fact]
    public async Task A_shop_can_be_moved_to_a_free_warehouse()
    {
        var shop = await Create(Request());

        var result = await Update(shop.Value.Id, UpdateRequest(warehouse: "KEFGRS"));

        Assert.False(result.IsError);
        Assert.Equal("KEFGRS", result.Value.WarehouseCode);
    }

    [Fact]
    public async Task A_shop_keeping_its_own_warehouse_is_not_blocked_by_itself()
    {
        var shop = await Create(Request());

        var result = await Update(shop.Value.Id, UpdateRequest(name: "Machipisa Branch"));

        Assert.False(result.IsError);
        Assert.Equal("Machipisa Branch", result.Value.Name);
        Assert.Equal("CORMACH2", result.Value.WarehouseCode);
    }

    [Fact]
    public async Task A_shop_cannot_take_another_shops_warehouse()
    {
        var machipisa = await Create(Request());
        await Create(Request(code: "FARM", name: "Farm", warehouse: "KEFSHOP"));

        var result = await Update(machipisa.Value.Id, UpdateRequest(warehouse: "KEFSHOP"));

        Assert.True(result.IsError);
        Assert.Equal("Shops.WarehouseAlreadyAssigned", result.FirstError.Code);
    }

    [Fact]
    public async Task Editing_a_shop_that_does_not_exist_is_a_not_found()
    {
        var result = await Update(4242, UpdateRequest());

        Assert.True(result.IsError);
        Assert.Equal("Shops.NotFound", result.FirstError.Code);
    }

    // ---- Closing and reopening ------------------------------------------------------------------

    [Fact]
    public async Task An_empty_shop_closes()
    {
        var shop = await Create(Request());

        var result = await SetActive(shop.Value.Id, isActive: false);

        Assert.False(result.IsError);
        Assert.False(result.Value.IsActive);
    }

    [Fact]
    public async Task A_shop_with_operators_on_it_cannot_be_closed()
    {
        // Their accounts would keep authenticating and then fail at the first sale, which reads to an
        // operator as a broken till rather than a closed shop.
        var shop = await Create(Request());
        await AddOperator(shop.Value.Id);
        await AddOperator(shop.Value.Id);

        var result = await SetActive(shop.Value.Id, isActive: false);

        Assert.True(result.IsError);
        Assert.Equal("Shops.HasAssignedOperators", result.FirstError.Code);
        Assert.Contains("2", result.FirstError.Description);
    }

    [Fact]
    public async Task A_shop_whose_only_operator_is_disabled_can_be_closed()
    {
        // A disabled account cannot sell, so it is not something closing the shop would strand.
        var shop = await Create(Request());
        await AddOperator(shop.Value.Id, isActive: false);

        var result = await SetActive(shop.Value.Id, isActive: false);

        Assert.False(result.IsError);
        Assert.False(result.Value.IsActive);
    }

    [Fact]
    public async Task Closing_a_shop_that_is_already_closed_is_not_an_error()
    {
        // So a double-click is not something the administrator has to read and dismiss.
        var shop = await Create(Request());
        await SetActive(shop.Value.Id, isActive: false);

        var result = await SetActive(shop.Value.Id, isActive: false);

        Assert.False(result.IsError);
        Assert.False(result.Value.IsActive);
    }

    [Fact]
    public async Task A_closed_shop_reopens_without_the_operator_check()
    {
        // The check guards closing only: reopening a shop strands nobody.
        var shop = await Create(Request());
        await SetActive(shop.Value.Id, isActive: false);
        await AddOperator(shop.Value.Id);

        var result = await SetActive(shop.Value.Id, isActive: true);

        Assert.False(result.IsError);
        Assert.True(result.Value.IsActive);
    }

    // ---- Reading --------------------------------------------------------------------------------

    [Fact]
    public async Task Closed_shops_are_left_out_of_the_default_listing()
    {
        // A picker offering a shop to assign an operator to must not show a closed one, or a till
        // could be opened on a counter that is not trading.
        var open = await Create(Request());
        var closed = await Create(Request(code: "FARM", name: "Farm", warehouse: "KEFSHOP"));
        await SetActive(closed.Value.Id, isActive: false);

        var result = await Shops(includeInactive: false);

        Assert.False(result.IsError);
        Assert.Equal(open.Value.Id, Assert.Single(result.Value).Id);
    }

    [Fact]
    public async Task Closed_shops_are_listed_when_asked_for()
    {
        var open = await Create(Request());
        var closed = await Create(Request(code: "FARM", name: "Farm", warehouse: "KEFSHOP"));
        await SetActive(closed.Value.Id, isActive: false);

        var result = await Shops(includeInactive: true);

        Assert.False(result.IsError);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, shop => shop.Id == open.Value.Id);
        Assert.Contains(result.Value, shop => shop.Id == closed.Value.Id);
    }

    [Fact]
    public async Task The_operator_count_reports_only_active_accounts()
    {
        var shop = await Create(Request());
        await AddOperator(shop.Value.Id);
        await AddOperator(shop.Value.Id, isActive: false);

        var result = await Shop(shop.Value.Id);

        Assert.False(result.IsError);
        Assert.Equal(1, result.Value.AssignedOperatorCount);
    }

    [Fact]
    public async Task Reading_a_shop_that_does_not_exist_is_a_not_found()
    {
        var result = await Shop(4242);

        Assert.True(result.IsError);
        Assert.Equal("Shops.NotFound", result.FirstError.Code);
    }

    // ---- Harness --------------------------------------------------------------------------------

    private static CreateShopRequest Request(
        string code = "MACHIPISA",
        string name = "Machipisa",
        string businessPartner = "SHOP-BP",
        string warehouse = "CORMACH2",
        string? costCentre = "CC-SHOP") => new()
        {
            Code = code,
            Name = name,
            BusinessPartnerCode = businessPartner,
            WarehouseCode = warehouse,
            CostCentreCode = costCentre,
        };

    private static UpdateShopRequest UpdateRequest(
        string name = "Machipisa",
        string businessPartner = "SHOP-BP",
        string warehouse = "CORMACH2",
        string? costCentre = "CC-SHOP") => new()
        {
            Name = name,
            BusinessPartnerCode = businessPartner,
            WarehouseCode = warehouse,
            CostCentreCode = costCentre,
        };

    private Task<ErrorOr.ErrorOr<ShopDto>> Create(CreateShopRequest request) =>
        new CreateShopHandler(_context, NullLogger<CreateShopHandler>.Instance)
            .Handle(new CreateShopCommand(request, AdminId), default);

    private Task<ErrorOr.ErrorOr<ShopDto>> Update(int shopId, UpdateShopRequest request) =>
        new UpdateShopHandler(_context, NullLogger<UpdateShopHandler>.Instance)
            .Handle(new UpdateShopCommand(shopId, request, AdminId), default);

    private Task<ErrorOr.ErrorOr<ShopDto>> SetActive(int shopId, bool isActive) =>
        new SetShopActiveHandler(_context, NullLogger<SetShopActiveHandler>.Instance)
            .Handle(new SetShopActiveCommand(shopId, isActive, AdminId), default);

    private Task<ErrorOr.ErrorOr<List<ShopDto>>> Shops(bool includeInactive) =>
        new GetShopsHandler(_context).Handle(new GetShopsQuery(includeInactive), default);

    private Task<ErrorOr.ErrorOr<ShopDto>> Shop(int shopId) =>
        new GetShopByIdHandler(_context).Handle(new GetShopByIdQuery(shopId), default);

    private async Task<User> AddOperator(int shopId, bool isActive = true)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"till{Guid.NewGuid():N}"[..12],
            PasswordHash = "not-used-here",
            Role = ApplicationRoles.TillOperator,
            IsActive = isActive,
            ShopId = shopId,
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // The handlers query fresh, but the shop this test already holds is tracked with a stale
        // Users collection; clearing keeps the count assertions honest rather than cached.
        _context.ChangeTracker.Clear();
        return user;
    }
}
