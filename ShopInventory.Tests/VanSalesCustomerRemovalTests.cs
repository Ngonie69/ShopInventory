using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.Features.RouteCustomers.Commands.DeleteRouteCustomer;
using ShopInventory.Features.VanSalesCompatibility.Commands.DeleteVanSalesCustomer;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// A rep taking a shop they no longer service off their handset.
///
/// The handset asks by code, because it has never been told the route customer id — the van-sales
/// customer payload carries a compatibility id derived from the code instead. Everything here is
/// about what that resolution is allowed to reach, and what happens when the same removal arrives
/// twice, which on a route is ordinary rather than exceptional: the rep deletes a shop out of
/// signal, and the queue posts it whenever it next can.
/// </summary>
public sealed class VanSalesCustomerRemovalTests : IDisposable
{
    private const string RouteCode = "VAN020";
    private const string OtherRouteCode = "VAN021";

    private static readonly Guid RepUser = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid HeadOfficeUser = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid InactiveUser = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesCustomerRemovalTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.AddRange(
            NewUser(RepUser, "van020", ApplicationRoles.Sales, RouteCode, isActive: true),
            // Customers come from head office for this one: no business partner of its own, so there
            // is nothing local to remove.
            NewUser(HeadOfficeUser, "office", ApplicationRoles.Admin, assignedBusinessPartnerCode: null, isActive: true),
            NewUser(InactiveUser, "van020old", ApplicationRoles.Sales, RouteCode, isActive: false));

        _context.RouteCustomers.AddRange(
            NewCustomer(RouteCode, "TUCK01", "Tuck Shop"),
            NewCustomer(RouteCode, "CAFE01", "Corner Cafe"),
            // Same code, different route. A code is only unique within one.
            NewCustomer(OtherRouteCode, "TUCK01", "A Tuck Shop On Another Route"));

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Removing_a_shop_by_code_deactivates_it_and_keeps_the_row()
    {
        var result = await DeleteAsync(RepUser, "TUCK01");

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);

        var customer = await FindAsync(RouteCode, "TUCK01");
        Assert.False(customer.IsActive);
        Assert.NotNull(customer.UpdatedAt);

        // The record itself is untouched. It is what the trend reporting reads.
        Assert.Equal("Tuck Shop", customer.Name);
    }

    /// <summary>
    /// The handset's own list is what the rep sees after the next sync, and it filters on the same
    /// flag — so this is the half of the change the rep actually asked for.
    /// </summary>
    [Fact]
    public async Task The_removed_shop_is_gone_from_what_the_handset_next_fetches()
    {
        await DeleteAsync(RepUser, "TUCK01");

        var user = await _context.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == RepUser);
        var onRoute = await VanSalesRouteCustomerScope.GetAssignedRouteCustomersAsync(
            _context, user, CancellationToken.None);

        Assert.Equal("CAFE01", Assert.Single(onRoute).Code);
    }

    /// <summary>
    /// The case the offline queue creates. A removal recorded without signal is posted whenever the
    /// handset next has some, and a queue that cannot tell "already done" from "failed" retries one
    /// for ever.
    /// </summary>
    [Fact]
    public async Task A_replayed_removal_succeeds_rather_than_failing_as_missing()
    {
        await DeleteAsync(RepUser, "TUCK01");
        var stamped = (await FindAsync(RouteCode, "TUCK01")).UpdatedAt;

        var replay = await DeleteAsync(RepUser, "TUCK01");

        Assert.False(replay.IsError, replay.IsError ? replay.FirstError.Description : null);

        // And it is a no-op rather than a second removal: the first attempt's stamp stands.
        var customer = await FindAsync(RouteCode, "TUCK01");
        Assert.False(customer.IsActive);
        Assert.Equal(stamped, customer.UpdatedAt);
    }

    /// <summary>
    /// A code names a row only within a route. Reaching another route's shop by naming its code is
    /// the failure this scoping exists to prevent, and it would be invisible from either handset.
    /// </summary>
    [Fact]
    public async Task A_rep_cannot_remove_an_identically_coded_shop_on_another_route()
    {
        await DeleteAsync(RepUser, "TUCK01");

        var otherRoute = await FindAsync(OtherRouteCode, "TUCK01");
        Assert.True(otherRoute.IsActive);
    }

    [Fact]
    public async Task An_account_served_by_head_office_is_refused_rather_than_silently_doing_nothing()
    {
        var result = await DeleteAsync(HeadOfficeUser, "TUCK01");

        Assert.True(result.IsError);
        Assert.Equal("RouteCustomers.RouteCustomersNotManagedHere", result.FirstError.Code);
    }

    [Fact]
    public async Task A_code_the_route_has_never_had_is_not_found()
    {
        var result = await DeleteAsync(RepUser, "NOSUCH");

        Assert.True(result.IsError);
        Assert.Equal("RouteCustomers.CodeNotFoundOnRoute", result.FirstError.Code);
    }

    [Fact]
    public async Task An_inactive_account_cannot_remove_anything()
    {
        var result = await DeleteAsync(InactiveUser, "TUCK01");

        Assert.True(result.IsError);
        Assert.Equal("RouteCustomers.UserInactive", result.FirstError.Code);
        Assert.True((await FindAsync(RouteCode, "TUCK01")).IsActive);
    }

    /// <summary>
    /// The code is the handset's, and a handset is a place where a stray space is easy to acquire.
    /// </summary>
    [Theory]
    [InlineData(" TUCK01 ")]
    [InlineData("TUCK01")]
    public async Task The_code_is_matched_after_trimming(string code)
    {
        var result = await DeleteAsync(RepUser, code);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.False((await FindAsync(RouteCode, "TUCK01")).IsActive);
    }

    // --- Fixtures ---

    private async Task<ErrorOr<Deleted>> DeleteAsync(Guid userId, string code)
    {
        var result = await new DeleteVanSalesCustomerHandler(_context, new RouteCustomerMediator(_context))
            .Handle(new DeleteVanSalesCustomerCommand(userId, code), CancellationToken.None);

        _context.ChangeTracker.Clear();
        return result;
    }

    private async Task<RouteCustomerEntity> FindAsync(string route, string code)
        => await _context.RouteCustomers
            .AsNoTracking()
            .SingleAsync(customer => customer.AssignedBusinessPartnerCode == route && customer.Code == code);

    private static User NewUser(
        Guid id,
        string username,
        string role,
        string? assignedBusinessPartnerCode,
        bool isActive) => new()
        {
            Id = id,
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = "x",
            Role = role,
            IsActive = isActive,
            AssignedWarehouseCode = assignedBusinessPartnerCode,
            AssignedCostCentreCode = assignedBusinessPartnerCode is null ? null : "CC020",
            AssignedBusinessPartnerCode = assignedBusinessPartnerCode
        };

    private static RouteCustomerEntity NewCustomer(string route, string code, string name) => new()
    {
        AssignedBusinessPartnerCode = route,
        Code = code,
        Name = name,
        IsActive = true,
        CreatedByUserId = RepUser,
        CreatedAt = new DateTime(2026, 8, 21, 9, 0, 0, DateTimeKind.Utc)
    };

    /// <summary>
    /// Runs the real removal rather than standing in for it. The point of the handler under test is
    /// that it hands off to the one place that deactivates a route customer, so a stub answering
    /// "deleted" without touching the row would assert nothing.
    /// </summary>
    private sealed class RouteCustomerMediator(ApplicationDbContext context) : IMediator
    {
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is DeleteRouteCustomerCommand command)
            {
                var result = await new DeleteRouteCustomerHandler(context).Handle(command, cancellationToken);
                return (TResponse)(object)result;
            }

            throw new NotSupportedException($"Unexpected request: {request.GetType().Name}");
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => throw new NotSupportedException();
    }
}
