using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.RouteCustomers.Commands.CreateRouteCustomer;
using ShopInventory.Features.RouteCustomers.Commands.DeleteRouteCustomer;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomers;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Deleting a route customer means "the vans stop selling to this shop", not "this shop never
/// traded". Those pulled in opposite directions while the delete removed the row: the van got what
/// it asked for and the office lost the shop out of its route trend, because the three sales tables
/// null their <c>RouteCustomerId</c> when the customer goes.
///
/// So the delete deactivates. These cover both halves of that — what the handset stops seeing, and
/// what the web keeps — plus the case the change creates, which is a shop being captured again
/// under a code the removed row is still holding.
/// </summary>
public sealed class RouteCustomerRemovalTests : IDisposable
{
    private const string RouteCode = "VAN010";
    private static readonly Guid VanUser = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    private readonly int _tuckShopId;

    public RouteCustomerRemovalTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = VanUser,
            Username = "van010",
            Email = "van010@example.com",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = true,
            AssignedWarehouseCode = RouteCode,
            AssignedCostCentreCode = "CC010",
            AssignedBusinessPartnerCode = RouteCode
        });

        var tuckShop = new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = RouteCode,
            Code = "TUCK01",
            Name = "Tuck Shop",
            Phone = "0771234567",
            IsActive = true,
            CreatedByUserId = VanUser,
            CreatedAt = new DateTime(2026, 5, 26, 12, 51, 0, DateTimeKind.Utc)
        };

        _context.RouteCustomers.Add(tuckShop);
        _context.SaveChanges();
        _context.ChangeTracker.Clear();

        _tuckShopId = tuckShop.Id;
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // --- What the delete does ---

    /// <summary>
    /// The row is the whole point. Its id is what the sales tables hold, and removing it is what
    /// used to detach a shop from everything it ever bought.
    /// </summary>
    [Fact]
    public async Task Deleting_a_route_customer_deactivates_it_and_keeps_the_row()
    {
        await DeleteAsync(_tuckShopId);

        var customer = await _context.RouteCustomers.SingleAsync(entity => entity.Id == _tuckShopId);
        Assert.False(customer.IsActive);
        Assert.NotNull(customer.UpdatedAt);

        // Untouched: the shop's own details are the record, not the thing being withdrawn.
        Assert.Equal("Tuck Shop", customer.Name);
        Assert.Equal("TUCK01", customer.Code);
    }

    /// <summary>
    /// The web list asks for active customers by default and gets the same answer a hard delete
    /// gave it. Asking for all of them is what now reaches the removed ones.
    /// </summary>
    [Fact]
    public async Task A_removed_customer_is_out_of_the_active_list_but_in_the_full_one()
    {
        await DeleteAsync(_tuckShopId);

        Assert.Empty(await ListAsync(activeOnly: true));

        var all = await ListAsync(activeOnly: false);
        Assert.False(Assert.Single(all).IsActive);
    }

    /// <summary>
    /// A stale list deleting twice is not reporting a problem — the second call asks for the state
    /// the row is already in. Failing it would only tell the caller its own list was old.
    /// </summary>
    [Fact]
    public async Task Removing_a_customer_twice_is_not_an_error()
    {
        await DeleteAsync(_tuckShopId);
        var stamped = (await _context.RouteCustomers.AsNoTracking()
            .SingleAsync(entity => entity.Id == _tuckShopId)).UpdatedAt;

        _context.ChangeTracker.Clear();
        await DeleteAsync(_tuckShopId);

        var customer = await _context.RouteCustomers.AsNoTracking()
            .SingleAsync(entity => entity.Id == _tuckShopId);
        Assert.False(customer.IsActive);

        // And it is a no-op, not a re-removal: the second call leaves the first one's stamp alone.
        Assert.Equal(stamped, customer.UpdatedAt);
    }

    [Fact]
    public async Task Deleting_a_customer_that_does_not_exist_is_still_a_not_found()
    {
        var result = await new DeleteRouteCustomerHandler(_context)
            .Handle(new DeleteRouteCustomerCommand(_tuckShopId + 999), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("RouteCustomers.NotFound", result.FirstError.Code);
    }

    // --- What the handset sees ---

    /// <summary>
    /// The reason the delete can deactivate at all: the van's customer list already filtered on
    /// <c>IsActive</c>, so the handset loses the shop on its next sync without a line of app change.
    /// </summary>
    [Fact]
    public async Task A_removed_customer_is_gone_from_the_van_it_was_on()
    {
        var user = await _context.Users.AsNoTracking().SingleAsync(entity => entity.Id == VanUser);

        Assert.Single(await VanSalesRouteCustomerScope.GetAssignedRouteCustomersAsync(
            _context, user, CancellationToken.None));

        await DeleteAsync(_tuckShopId);

        Assert.Empty(await VanSalesRouteCustomerScope.GetAssignedRouteCustomersAsync(
            _context, user, CancellationToken.None));
    }

    // --- Capturing the shop again ---

    /// <summary>
    /// A shop that starts buying again. Its code is still held by the removed row, so a second row
    /// is impossible — the unique (route, code) index says so — and the point of reusing the first
    /// one is that the shop's takings from before are still attached to it.
    /// </summary>
    [Fact]
    public async Task Capturing_a_removed_customer_again_brings_back_its_own_row()
    {
        await DeleteAsync(_tuckShopId);

        var created = await CreateAsync(new CreateRouteCustomerRequest
        {
            Code = "TUCK01",
            Name = "Tuck Shop",
            Phone = "0779999999"
        });

        Assert.Equal(_tuckShopId, created.Id);
        Assert.True(created.IsActive);
        Assert.Equal("0779999999", created.Phone);

        Assert.Equal(1, await _context.RouteCustomers.CountAsync());

        // The capture date is when this shop first went on the route, and that was not today.
        Assert.Equal(new DateTime(2026, 5, 26, 12, 51, 0, DateTimeKind.Utc), created.CreatedAt);
        Assert.NotNull(created.UpdatedAt);
    }

    /// <summary>
    /// The handset usually sends no code and lets the server derive one from the name. That path
    /// has to find the removed row too, or re-capturing a shop silently forks it into TUCK01 and
    /// TUCK-SHOP-2 with the history on the one nobody can see.
    /// </summary>
    [Fact]
    public async Task Capturing_a_removed_customer_by_name_alone_brings_back_its_own_row()
    {
        // The code the generator derives from this name, which is what the removed row is holding.
        _context.RouteCustomers.Add(new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = RouteCode,
            Code = "CORNERSTORE",
            Name = "Corner Store",
            IsActive = false
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var created = await CreateAsync(new CreateRouteCustomerRequest { Name = "Corner Store" });

        Assert.Equal("CORNERSTORE", created.Code);
        Assert.True(created.IsActive);
        Assert.Equal(2, await _context.RouteCustomers.CountAsync());
    }

    /// <summary>A code a trading shop is using is still taken, removals or not.</summary>
    [Fact]
    public async Task A_code_an_active_customer_holds_is_still_rejected()
    {
        var result = await new CreateRouteCustomerHandler(
                _context, new NoOpNotificationService(), NullLogger<CreateRouteCustomerHandler>.Instance)
            .Handle(
                new CreateRouteCustomerCommand(
                    new CreateRouteCustomerRequest { Code = "TUCK01", Name = "Somebody Else" },
                    VanUser),
                CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("RouteCustomers.CodeAlreadyExists", result.FirstError.Code);
    }

    /// <summary>
    /// A different shop on a route where the name's code is taken by a trading one. This is the
    /// suffix the generator has always added, and it must still skip the removed rows rather than
    /// reuse one that belongs to a different shop.
    /// </summary>
    [Fact]
    public async Task A_new_shop_named_like_a_removed_one_gets_its_own_row()
    {
        _context.RouteCustomers.Add(new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = RouteCode,
            Code = "TUCKSHOP-2",
            Name = "Tuck Shop",
            IsActive = false
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // TUCKSHOP is free, so this is a plain create and neither removed row is touched.
        var created = await CreateAsync(new CreateRouteCustomerRequest { Name = "Tuck Shop" });

        Assert.Equal("TUCKSHOP", created.Code);
        Assert.Equal(3, await _context.RouteCustomers.CountAsync());
    }

    // --- Fixtures ---

    private async Task DeleteAsync(int routeCustomerId)
    {
        var result = await new DeleteRouteCustomerHandler(_context)
            .Handle(new DeleteRouteCustomerCommand(routeCustomerId), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        _context.ChangeTracker.Clear();
    }

    private async Task<RouteCustomerDto> CreateAsync(CreateRouteCustomerRequest request)
    {
        var result = await new CreateRouteCustomerHandler(
                _context, new NoOpNotificationService(), NullLogger<CreateRouteCustomerHandler>.Instance)
            .Handle(new CreateRouteCustomerCommand(request, VanUser), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        _context.ChangeTracker.Clear();
        return result.Value;
    }

    private async Task<List<RouteCustomerDto>> ListAsync(bool activeOnly)
    {
        var result = await new GetRouteCustomersHandler(_context)
            .Handle(new GetRouteCustomersQuery(RouteCode, activeOnly), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }
}
