using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSales;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins who may read till takings, and whose.
///
/// The hole this closes: <c>GET /api/DesktopIntegration/sales</c> took its warehouse straight off the
/// query string and checked it against nobody. The only guard was the class-level "ApiAccess" policy,
/// which admits every staff role — so a merchandiser, a driver or a van rep could read any shop's
/// sales, with every money column, by editing one parameter.
///
/// The assertions worth keeping are the ones about a caller who <em>omits</em> the warehouse. Refusing
/// a wrong one is the obvious half; the quiet half is that omitting it must narrow to the caller's own
/// shop rather than fall through to every shop, which is what the unfixed handler did.
/// </summary>
public sealed class DesktopSalesReadScopeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    private int _machipisaId;
    private int _farmId;

    public DesktopSalesReadScopeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        var machipisa = new ShopEntity
        {
            Code = "MACHIPISA",
            Name = "Machipisa",
            BusinessPartnerCode = "MACH-BP",
            WarehouseCode = "CORMACH2",
            IsActive = true,
        };
        var farm = new ShopEntity
        {
            Code = "FARM",
            Name = "Farm",
            BusinessPartnerCode = "FARM-BP",
            WarehouseCode = "KEFSHOP",
            IsActive = true,
        };

        _context.Shops.AddRange(machipisa, farm);
        _context.SaveChanges();

        _machipisaId = machipisa.Id;
        _farmId = farm.Id;

        // One sale at each shop, so a leak across the scope is visible as a row rather than a count.
        _context.DesktopSales.AddRange(
            Sale("MACH-001", "CORMACH2", "MACH-BP"),
            Sale("FARM-001", "KEFSHOP", "FARM-BP"));
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---- A till operator sees its own shop, and only its own ------------------------------------

    [Fact]
    public async Task A_till_operator_omitting_the_warehouse_sees_only_its_own_shop()
    {
        // The quiet half of the hole. Before the scope existed, omitting the parameter listed every
        // shop's takings — so the leak needed no crafted request at all.
        var operatorId = await AddUser(ApplicationRoles.TillOperator, shopId: _machipisaId);

        var result = await List(operatorId, warehouseCode: null);

        Assert.False(result.IsError);
        Assert.Equal("MACH-001", Assert.Single(result.Value.Sales).ExternalReferenceId);
        Assert.Equal(1, result.Value.TotalCount);
    }

    [Fact]
    public async Task A_till_operator_naming_its_own_warehouse_is_allowed()
    {
        var operatorId = await AddUser(ApplicationRoles.TillOperator, shopId: _machipisaId);

        var result = await List(operatorId, warehouseCode: "CORMACH2");

        Assert.False(result.IsError);
        Assert.Equal("MACH-001", Assert.Single(result.Value.Sales).ExternalReferenceId);
    }

    [Fact]
    public async Task A_till_operator_naming_its_own_warehouse_in_another_case_is_allowed()
    {
        var operatorId = await AddUser(ApplicationRoles.TillOperator, shopId: _machipisaId);

        var result = await List(operatorId, warehouseCode: "cormach2");

        Assert.False(result.IsError);
        Assert.Equal("MACH-001", Assert.Single(result.Value.Sales).ExternalReferenceId);
    }

    [Fact]
    public async Task A_till_operator_naming_another_shops_warehouse_is_refused()
    {
        // Refused rather than quietly narrowed: a till headed with one warehouse and filled with
        // another's takings is worse than an error, and silently rewriting hides the probe.
        var operatorId = await AddUser(ApplicationRoles.TillOperator, shopId: _machipisaId);

        var result = await List(operatorId, warehouseCode: "KEFSHOP");

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SalesReadOutsideScope", result.FirstError.Code);
    }

    [Fact]
    public async Task A_till_operator_with_no_shop_cannot_list_at_all()
    {
        var operatorId = await AddUser(ApplicationRoles.TillOperator, shopId: null);

        var result = await List(operatorId, warehouseCode: null);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SalesReadNotPermitted", result.FirstError.Code);
    }

    [Fact]
    public async Task A_deactivated_till_operator_cannot_list()
    {
        var operatorId = await AddUser(ApplicationRoles.TillOperator, shopId: _machipisaId, isActive: false);

        var result = await List(operatorId, warehouseCode: null);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.Unauthenticated", result.FirstError.Code);
    }

    [Fact]
    public async Task A_caller_the_token_names_but_the_table_does_not_cannot_list()
    {
        var result = await List(Guid.NewGuid(), warehouseCode: null);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.Unauthenticated", result.FirstError.Code);
    }

    // ---- The roles that had no business here in the first place ---------------------------------

    [Theory]
    [InlineData(ApplicationRoles.Merchandiser)]
    [InlineData(ApplicationRoles.Driver)]
    [InlineData(ApplicationRoles.PodOperator)]
    [InlineData(ApplicationRoles.Adr)]
    [InlineData(ApplicationRoles.Sales)]
    [InlineData(ApplicationRoles.CartVendor)]
    [InlineData(ApplicationRoles.Lab)]
    [InlineData(ApplicationRoles.StockController)]
    [InlineData(ApplicationRoles.DepotController)]
    public async Task A_handset_or_warehouse_role_cannot_read_till_takings(string role)
    {
        // Every one of these is in ApiAccessRoles, which was the endpoint's only guard, so every one
        // of them could read every shop's money before the scope existed.
        var userId = await AddUser(role, shopId: null);

        var result = await List(userId, warehouseCode: null);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SalesReadNotPermitted", result.FirstError.Code);
    }

    // ---- The consoles ---------------------------------------------------------------------------

    [Theory]
    [InlineData(ApplicationRoles.Admin)]
    [InlineData(ApplicationRoles.ApiUser)]
    [InlineData(ApplicationRoles.Cashier)]
    [InlineData(ApplicationRoles.Manager)]
    public async Task A_console_role_reads_across_every_shop(string role)
    {
        var userId = await AddUser(role, shopId: null);

        var result = await List(userId, warehouseCode: null);

        Assert.False(result.IsError);
        Assert.Equal(2, result.Value.TotalCount);
    }

    [Fact]
    public async Task A_console_role_can_still_filter_to_one_warehouse()
    {
        // The parameter stays a filter for anyone entitled to the whole set.
        var userId = await AddUser(ApplicationRoles.Cashier, shopId: null);

        var result = await List(userId, warehouseCode: "KEFSHOP");

        Assert.False(result.IsError);
        Assert.Equal("FARM-001", Assert.Single(result.Value.Sales).ExternalReferenceId);
    }

    // ---- The backfill case ----------------------------------------------------------------------

    [Fact]
    public async Task A_cashier_pointed_at_a_shop_is_confined_to_it()
    {
        // The case that matters for the deploy: the accounts working tills today hold Cashier, and the
        // backfill points them at a shop rather than changing their role. Were the scope keyed on the
        // TillOperator role alone, every backfilled till would keep reading every shop's money until
        // somebody remembered to change its role too.
        var cashierId = await AddUser(ApplicationRoles.Cashier, shopId: _farmId);

        var result = await List(cashierId, warehouseCode: null);

        Assert.False(result.IsError);
        Assert.Equal("FARM-001", Assert.Single(result.Value.Sales).ExternalReferenceId);
    }

    [Fact]
    public async Task An_administrator_assigned_to_a_shop_still_reads_every_shop()
    {
        // An administrator at a counter is still an administrator.
        var adminId = await AddUser(ApplicationRoles.Admin, shopId: _machipisaId);

        var result = await List(adminId, warehouseCode: null);

        Assert.False(result.IsError);
        Assert.Equal(2, result.Value.TotalCount);
    }

    [Fact]
    public async Task An_operator_at_a_shop_with_no_warehouse_is_refused_rather_than_widened()
    {
        // Widening here would turn a misconfigured shop into a way to read every other shop's takings.
        var broken = new ShopEntity
        {
            Code = "BROKEN",
            Name = "Broken",
            BusinessPartnerCode = "BROKEN-BP",
            WarehouseCode = "   ",
            IsActive = true,
        };
        _context.Shops.Add(broken);
        await _context.SaveChangesAsync();

        var operatorId = await AddUser(ApplicationRoles.TillOperator, shopId: broken.Id);

        var result = await List(operatorId, warehouseCode: null);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.ShopMisconfigured", result.FirstError.Code);
    }

    [Fact]
    public void A_shop_named_but_not_loaded_is_a_fault_rather_than_a_widened_scope()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "till",
            PasswordHash = "x",
            Role = ApplicationRoles.TillOperator,
            IsActive = true,
            ShopId = _machipisaId,
            Shop = null,
        };

        Assert.Throws<InvalidOperationException>(() => DesktopSalesReadScopeResolver.Resolve(user));
    }

    // ---- Harness --------------------------------------------------------------------------------

    private static DesktopSaleEntity Sale(string reference, string warehouseCode, string cardCode) => new()
    {
        ExternalReferenceId = reference,
        SourceSystem = "KefShop",
        CardCode = cardCode,
        WarehouseCode = warehouseCode,
        DocDate = new DateTime(2026, 9, 1),
        TotalAmount = 100m,
        VatAmount = 13m,
        AmountPaid = 100m,
        Currency = "USD",
        CreatedAt = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
    };

    private async Task<Guid> AddUser(string role, int? shopId, bool isActive = true)
    {
        var id = Guid.NewGuid();
        _context.Users.Add(new User
        {
            Id = id,
            Username = $"u{id:N}"[..12],
            PasswordHash = "x",
            Role = role,
            IsActive = isActive,
            ShopId = shopId,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return id;
    }

    private Task<ErrorOr.ErrorOr<DesktopSalesListResult>> List(Guid callerId, string? warehouseCode) =>
        new GetDesktopSalesHandler(_context)
            .Handle(new GetDesktopSalesQuery(callerId, warehouseCode), CancellationToken.None);
}
