using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.RouteCustomers.Commands.SetRouteCustomerVisitDays;
using ShopInventory.Features.VanSalesOrders;
using ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerProfile;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The screen a customer sees when the app opens: who they are, which van calls on them, and when
/// they must order by.
///
/// The route is the interesting part. A shop does not store its route — it belongs to a van through
/// <c>AssignedBusinessPartnerCode</c>, and the van's user account carries the route. Deriving it
/// through that join means a shop moved between vans follows its new van without anyone having to
/// remember a second field, which is precisely what a denormalised copy would fail to do.
///
/// Everything here also has to degrade rather than fail. Most customers begin with no calling days
/// and some vans have no route recorded, and neither may stop a shop ordering.
/// </summary>
public sealed class VanSalesCustomerProfileTests : IDisposable
{
    private static readonly Guid VanUser = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesCustomerProfileTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task The_profile_names_the_shop_the_token_belongs_to()
    {
        var accountId = await GivenCustomerAsync();

        var result = await ProfileAsync(accountId);

        Assert.False(result.IsError);
        Assert.Equal("CUST-1", result.Value.CustomerCode);
        Assert.Equal("Shop CUST-1", result.Value.CustomerName);
    }

    [Fact]
    public async Task The_route_is_derived_through_the_van_that_serves_the_shop()
    {
        var accountId = await GivenCustomerAsync();
        await GivenVanOnRouteAsync("BP-1", "GUR", "Guruve", "UPC");

        var result = await ProfileAsync(accountId);

        Assert.Equal("GUR", result.Value.RouteCode);
        Assert.Equal("Guruve", result.Value.RouteName);
        Assert.Equal("UPC", result.Value.Territory);
    }

    [Fact]
    public async Task Moving_a_shop_to_another_van_moves_its_route_with_it()
    {
        // The reason the route is not copied onto the customer. One field changes and the answer
        // follows; a stored copy would keep pointing at the old van until somebody noticed.
        var accountId = await GivenCustomerAsync();
        await GivenVanOnRouteAsync("BP-1", "GUR", "Guruve", "UPC");
        await GivenVanOnRouteAsync("BP-2", "BIN", "Bindura", "UPC", Guid.Parse("44444444-4444-4444-4444-444444444444"));

        await _context.RouteCustomers.ExecuteUpdateAsync(
            s => s.SetProperty(c => c.AssignedBusinessPartnerCode, "BP-2"));
        _context.ChangeTracker.Clear();

        var result = await ProfileAsync(accountId);

        Assert.Equal("BIN", result.Value.RouteCode);
    }

    [Fact]
    public async Task A_van_with_no_route_recorded_does_not_break_the_profile()
    {
        // The route is a label on this screen, not a permission. A shop whose van is between
        // accounts still has to be able to order.
        var accountId = await GivenCustomerAsync();

        var result = await ProfileAsync(accountId);

        Assert.False(result.IsError);
        Assert.Null(result.Value.RouteCode);
        Assert.True(result.Value.IsOrderingOpen);
    }

    [Fact]
    public async Task Calling_days_reach_the_profile_and_produce_a_deadline()
    {
        var accountId = await GivenCustomerAsync();
        await SetVisitDaysAsync(1, [DayOfWeek.Tuesday, DayOfWeek.Friday]);

        var result = await ProfileAsync(accountId);

        Assert.True(result.Value.HasSchedule);
        Assert.Equal([DayOfWeek.Tuesday, DayOfWeek.Friday], result.Value.VisitDays);
        Assert.NotNull(result.Value.NextVisitDate);
        Assert.NotNull(result.Value.OrdersCloseAtUtc);
        Assert.True(result.Value.IsOrderingOpen);
    }

    [Fact]
    public async Task A_shop_with_no_calling_days_can_still_order()
    {
        var accountId = await GivenCustomerAsync();

        var result = await ProfileAsync(accountId);

        Assert.False(result.Value.HasSchedule);
        Assert.Empty(result.Value.VisitDays);
        Assert.Null(result.Value.NextVisitDate);
        Assert.True(result.Value.IsOrderingOpen);
    }

    [Fact]
    public async Task A_deactivated_sign_in_gets_no_profile()
    {
        var accountId = await GivenCustomerAsync();
        await _context.VanSalesCustomerAccounts.ExecuteUpdateAsync(
            s => s.SetProperty(a => a.IsActive, false));
        _context.ChangeTracker.Clear();

        var result = await ProfileAsync(accountId);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesCustomerAuth.AccountInactive", result.FirstError.Code);
    }

    [Fact]
    public async Task A_shop_deactivated_by_head_office_gets_no_profile()
    {
        // The sign-in may still be active while the customer behind it has been retired. Ordering
        // must stop with the customer, not with the phone.
        var accountId = await GivenCustomerAsync();
        await _context.RouteCustomers.ExecuteUpdateAsync(s => s.SetProperty(c => c.IsActive, false));
        _context.ChangeTracker.Clear();

        var result = await ProfileAsync(accountId);

        Assert.True(result.IsError);
    }

    // ── Managing the calling days ───────────────────────

    [Fact]
    public async Task Setting_calling_days_replaces_what_was_there()
    {
        await GivenCustomerAsync();

        await SetVisitDaysAsync(1, [DayOfWeek.Monday, DayOfWeek.Thursday]);
        var result = await SetVisitDaysAsync(1, [DayOfWeek.Tuesday]);

        Assert.False(result.IsError);
        Assert.Equal([DayOfWeek.Tuesday], result.Value.VisitDays);
        Assert.Equal(1, await _context.RouteCustomerVisitDays.CountAsync());
    }

    [Fact]
    public async Task The_same_day_sent_twice_is_stored_once()
    {
        // A client repeating itself is not a different instruction, and the unique index would
        // otherwise turn it into an error an operator cannot act on.
        await GivenCustomerAsync();

        var result = await SetVisitDaysAsync(1, [DayOfWeek.Tuesday, DayOfWeek.Tuesday]);

        Assert.False(result.IsError);
        Assert.Equal([DayOfWeek.Tuesday], result.Value.VisitDays);
        Assert.Equal(1, await _context.RouteCustomerVisitDays.CountAsync());
    }

    [Fact]
    public async Task Clearing_the_calling_days_is_allowed()
    {
        // "Not yet known" is a real state, and the one every customer starts in.
        await GivenCustomerAsync();
        await SetVisitDaysAsync(1, [DayOfWeek.Tuesday]);

        var result = await SetVisitDaysAsync(1, []);

        Assert.False(result.IsError);
        Assert.Empty(result.Value.VisitDays);
        Assert.Equal(0, await _context.RouteCustomerVisitDays.CountAsync());
    }

    [Fact]
    public async Task Setting_days_for_a_shop_that_does_not_exist_reports_not_found()
    {
        var result = await SetVisitDaysAsync(404, [DayOfWeek.Tuesday]);

        Assert.True(result.IsError);
        Assert.Equal("RouteCustomers.NotFound", result.FirstError.Code);
    }

    // ── Fixture ─────────────────────────────────────────

    private async Task<ErrorOr.ErrorOr<VanSalesCustomerProfileResult>> ProfileAsync(int accountId)
    {
        var handler = new GetVanSalesCustomerProfileHandler(_context, new FixedRules());
        var result = await handler.Handle(new GetVanSalesCustomerProfileQuery(accountId), default);
        _context.ChangeTracker.Clear();
        return result;
    }

    private async Task<ErrorOr.ErrorOr<Features.RouteCustomers.RouteCustomerVisitDaysResult>> SetVisitDaysAsync(
        int routeCustomerId,
        DayOfWeek[] days)
    {
        var handler = new SetRouteCustomerVisitDaysHandler(
            _context,
            NullLogger<SetRouteCustomerVisitDaysHandler>.Instance);

        var result = await handler.Handle(
            new SetRouteCustomerVisitDaysCommand(routeCustomerId, days),
            default);
        _context.ChangeTracker.Clear();
        return result;
    }

    private async Task<int> GivenCustomerAsync()
    {
        _context.RouteCustomers.Add(new RouteCustomerEntity
        {
            Id = 1,
            AssignedBusinessPartnerCode = "BP-1",
            Code = "CUST-1",
            Name = "Shop CUST-1",
            Phone = "+263771234567",
            Address = "12 Main Street",
            IsActive = true
        });
        await _context.SaveChangesAsync();

        var account = new VanSalesCustomerAccountEntity
        {
            RouteCustomerId = 1,
            PhoneE164 = "+263771234567",
            DisplayName = "Owner",
            IsActive = true
        };
        _context.VanSalesCustomerAccounts.Add(account);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        return account.Id;
    }

    private async Task GivenVanOnRouteAsync(
        string businessPartnerCode,
        string routeCode,
        string routeName,
        string territory,
        Guid? userId = null)
    {
        var route = new RouteEntity
        {
            Code = routeCode,
            Name = routeName,
            Territory = territory,
            IsActive = true
        };
        _context.Routes.Add(route);
        await _context.SaveChangesAsync();

        _context.Users.Add(new User
        {
            Id = userId ?? VanUser,
            Username = $"van-{routeCode}",
            Email = $"van-{routeCode}@example.test",
            PasswordHash = "x",
            Role = ApplicationRoles.Sales,
            AssignedBusinessPartnerCode = businessPartnerCode,
            RouteId = route.Id,
            IsActive = true
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    /// <summary>The rules are exercised elsewhere; here they just have to be numbers.</summary>
    private sealed class FixedRules : IVanSalesOrderingPolicy
    {
        public Task<VanSalesOrderingRules> GetRulesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new VanSalesOrderingRules(8, 1, 10m));
    }

    /// <summary>
    /// <see cref="DailyStockSnapshotItemEntity.Version"/> is <c>[Timestamp]</c>, mapped by Npgsql to
    /// the store-generated <c>xmin</c>, which SQLite has no equivalent for. Nothing here touches it.
    /// </summary>
    private sealed class SqliteApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<DailyStockSnapshotItemEntity>()
                .Property(item => item.Version)
                .ValueGeneratedNever()
                .IsConcurrencyToken(false);
        }
    }
}
