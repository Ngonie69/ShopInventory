using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerSales;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesCustomerHistory;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The handset's customer drill-down.
///
/// The read itself is the office's, already covered by the route customer sales tests. What is new
/// here is the resolution in front of it: a handset names a shop by code, codes repeat across routes,
/// and this is what decides which route's shop a given rep is allowed to open. Get that wrong and one
/// rep reads another route's trading history, which is not something either of them could notice.
/// </summary>
public sealed class VanSalesCustomerHistoryTests : IDisposable
{
    private const string RouteCode = "VAN040";
    private const string OtherRouteCode = "VAN041";

    private static readonly Guid RepUser = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid HeadOfficeUser = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesCustomerHistoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.AddRange(
            NewUser(RepUser, "van040", ApplicationRoles.Sales, RouteCode),
            NewUser(HeadOfficeUser, "office40", ApplicationRoles.Admin, assignedBusinessPartnerCode: null));

        _context.RouteCustomers.AddRange(
            NewCustomer(RouteCode, "TUCK01", "Tuck Shop", isActive: true),
            NewCustomer(RouteCode, "GONE01", "Closed Down", isActive: false),
            NewCustomer(OtherRouteCode, "TUCK01", "A Tuck Shop On Another Route", isActive: true));

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_rep_opens_the_shop_on_their_own_route()
    {
        var result = await HistoryAsync(RepUser, "TUCK01");

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal("Tuck Shop", result.Value.Customer.Name);
        Assert.Equal(RouteCode, result.Value.Customer.AssignedBusinessPartnerCode);
    }

    /// <summary>
    /// The one that matters. Both routes have a "TUCK01", and the rep must reach only their own.
    /// </summary>
    [Fact]
    public async Task A_rep_never_reads_an_identically_coded_shop_on_another_route()
    {
        var result = await HistoryAsync(RepUser, "TUCK01");

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.NotEqual("A Tuck Shop On Another Route", result.Value.Customer.Name);
        Assert.NotEqual(OtherRouteCode, result.Value.Customer.AssignedBusinessPartnerCode);
    }

    /// <summary>
    /// A card open when a removal lands still shows what the shop bought. The history is a read, and
    /// answering it with "not found" would look to the rep like the trading had gone too.
    /// </summary>
    [Fact]
    public async Task A_removed_shop_still_opens()
    {
        var result = await HistoryAsync(RepUser, "GONE01");

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal("Closed Down", result.Value.Customer.Name);
    }

    [Fact]
    public async Task A_code_the_route_has_never_had_is_not_found()
    {
        var result = await HistoryAsync(RepUser, "NOSUCH");

        Assert.True(result.IsError);
        Assert.Equal("RouteCustomers.CodeNotFoundOnRoute", result.FirstError.Code);
    }

    [Fact]
    public async Task An_account_served_by_head_office_is_refused()
    {
        var result = await HistoryAsync(HeadOfficeUser, "TUCK01");

        Assert.True(result.IsError);
        Assert.Equal("RouteCustomers.RouteCustomersNotManagedHere", result.FirstError.Code);
    }

    // --- Fixtures ---

    private async Task<ErrorOr<RouteCustomerSalesDetailDto>> HistoryAsync(Guid userId, string code)
        => await new GetVanSalesCustomerHistoryHandler(_context, new SalesMediator(_context))
            .Handle(new GetVanSalesCustomerHistoryQuery(userId, code, null, null), CancellationToken.None);

    private static User NewUser(Guid id, string username, string role, string? assignedBusinessPartnerCode) => new()
    {
        Id = id,
        Username = username,
        Email = $"{username}@example.com",
        PasswordHash = "x",
        Role = role,
        IsActive = true,
        AssignedWarehouseCode = assignedBusinessPartnerCode,
        AssignedCostCentreCode = assignedBusinessPartnerCode is null ? null : "CC040",
        AssignedBusinessPartnerCode = assignedBusinessPartnerCode
    };

    private static RouteCustomerEntity NewCustomer(string route, string code, string name, bool isActive) => new()
    {
        AssignedBusinessPartnerCode = route,
        Code = code,
        Name = name,
        IsActive = isActive,
        CreatedByUserId = RepUser,
        CreatedAt = new DateTime(2026, 8, 21, 9, 0, 0, DateTimeKind.Utc)
    };

    /// <summary>
    /// Runs the office's real read, so which customer the resolution landed on is asserted against
    /// what that read actually returns rather than against a stub echoing the id back.
    /// </summary>
    private sealed class SalesMediator(ApplicationDbContext context) : IMediator
    {
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is GetRouteCustomerSalesQuery query)
            {
                var result = await new GetRouteCustomerSalesHandler(context).Handle(query, cancellationToken);
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
