using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.RouteCustomers.Commands.UpdateRouteCustomer;
using ShopInventory.Features.VanSalesCompatibility.Commands.UpdateVanSalesCustomer;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A rep correcting what the handset holds for a shop on their route.
///
/// Most of these are about what the request cannot reach. The command it delegates to is the
/// administrator's, which may move a customer between routes, rename its code and set its active
/// flag — so the guarantee that a handset cannot do any of those is not a property of the request
/// shape alone, it is a property of this handler filling those three fields off the row. That is
/// exactly the kind of thing a later edit could undo without any visible symptom, which is why each
/// one is asserted rather than assumed.
/// </summary>
public sealed class VanSalesCustomerEditTests : IDisposable
{
    private const string RouteCode = "VAN030";
    private const string OtherRouteCode = "VAN031";

    private static readonly Guid RepUser = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid HeadOfficeUser = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesCustomerEditTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.AddRange(
            NewUser(RepUser, "van030", ApplicationRoles.Sales, RouteCode),
            NewUser(HeadOfficeUser, "office30", ApplicationRoles.Admin, assignedBusinessPartnerCode: null));

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
    public async Task A_rep_can_correct_the_details_they_find_wrong_at_the_counter()
    {
        var result = await UpdateAsync(RepUser, "TUCK01", new VanSalesUpdateCustomerRequest
        {
            Name = "Tuckshop Express",
            Phone = "0779999999",
            Address = "12 Second Street",
            Email = "tuck@example.com",
            VatNumber = "220123456"
        });

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);

        var customer = await FindAsync(RouteCode, "TUCK01");
        Assert.Equal("Tuckshop Express", customer.Name);
        Assert.Equal("0779999999", customer.Phone);
        Assert.Equal("12 Second Street", customer.Address);
        Assert.Equal("tuck@example.com", customer.Email);
        Assert.Equal("220123456", customer.VatNumber);
    }

    /// <summary>
    /// The reply is what the handset writes back into its own cache, so it has to be the shop as it
    /// now stands rather than as it was asked to be.
    /// </summary>
    [Fact]
    public async Task The_reply_carries_the_corrected_shop()
    {
        var result = await UpdateAsync(RepUser, "TUCK01", new VanSalesUpdateCustomerRequest
        {
            Name = "Tuckshop Express",
            Phone = "0779999999"
        });

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal("Tuckshop Express", result.Value.Name);
        Assert.Equal("0779999999", result.Value.Phone);
        Assert.Equal("TUCK01", result.Value.Code);
        Assert.Equal(1, result.Value.Status);
    }

    /// <summary>
    /// The code is the identity every sale, every queued document and the handset's own cache names
    /// the shop by. Renaming it from a handset would detach all of them at once, and nothing on the
    /// screen would look wrong.
    /// </summary>
    [Fact]
    public async Task An_edit_cannot_rename_the_code()
    {
        await UpdateAsync(RepUser, "TUCK01", new VanSalesUpdateCustomerRequest { Name = "Tuckshop Express" });

        var customer = await FindAsync(RouteCode, "TUCK01");
        Assert.Equal("TUCK01", customer.Code);
    }

    /// <summary>
    /// Moving a shop to another van is not a correction, and it is not a rep's to make.
    /// </summary>
    [Fact]
    public async Task An_edit_cannot_move_the_shop_to_another_route()
    {
        await UpdateAsync(RepUser, "TUCK01", new VanSalesUpdateCustomerRequest { Name = "Tuckshop Express" });

        var customer = await FindAsync(RouteCode, "TUCK01");
        Assert.Equal(RouteCode, customer.AssignedBusinessPartnerCode);
    }

    /// <summary>
    /// Removal has its own permission and its own audience. An edit that could set the flag would be
    /// a way round both.
    /// </summary>
    [Fact]
    public async Task An_edit_leaves_the_active_flag_alone()
    {
        await UpdateAsync(RepUser, "TUCK01", new VanSalesUpdateCustomerRequest { Name = "Tuckshop Express" });

        Assert.True((await FindAsync(RouteCode, "TUCK01")).IsActive);
    }

    /// <summary>
    /// And the other direction: a removed shop is not there to be edited, so an edit cannot be used
    /// to quietly bring one back.
    /// </summary>
    [Fact]
    public async Task A_removed_shop_cannot_be_edited_back_into_the_route()
    {
        var result = await UpdateAsync(RepUser, "GONE01", new VanSalesUpdateCustomerRequest { Name = "Reopened" });

        Assert.True(result.IsError);
        Assert.Equal("RouteCustomers.CodeNotFoundOnRoute", result.FirstError.Code);

        var customer = await FindAsync(RouteCode, "GONE01");
        Assert.False(customer.IsActive);
        Assert.Equal("Closed Down", customer.Name);
    }

    [Fact]
    public async Task A_rep_cannot_edit_an_identically_coded_shop_on_another_route()
    {
        await UpdateAsync(RepUser, "TUCK01", new VanSalesUpdateCustomerRequest { Name = "Tuckshop Express" });

        Assert.Equal("A Tuck Shop On Another Route", (await FindAsync(OtherRouteCode, "TUCK01")).Name);
    }

    [Fact]
    public async Task An_account_served_by_head_office_is_refused()
    {
        var result = await UpdateAsync(HeadOfficeUser, "TUCK01", new VanSalesUpdateCustomerRequest { Name = "Anything" });

        Assert.True(result.IsError);
        Assert.Equal("RouteCustomers.RouteCustomersNotManagedHere", result.FirstError.Code);
    }

    /// <summary>
    /// The name is the one field with nothing to fall back on, and an empty one would leave a blank
    /// row on every screen that lists the route.
    /// </summary>
    [Fact]
    public async Task An_empty_name_is_refused()
    {
        var result = await UpdateAsync(RepUser, "TUCK01", new VanSalesUpdateCustomerRequest { Name = "   " });

        Assert.True(result.IsError);
        Assert.Equal("Tuck Shop", (await FindAsync(RouteCode, "TUCK01")).Name);
    }

    // --- Fixtures ---

    private async Task<ErrorOr<VanSalesShopDto>> UpdateAsync(
        Guid userId,
        string code,
        VanSalesUpdateCustomerRequest request)
    {
        var result = await new UpdateVanSalesCustomerHandler(
                _context,
                new RouteCustomerMediator(_context),
                BusinessPartners())
            .Handle(new UpdateVanSalesCustomerCommand(userId, code, request), CancellationToken.None);

        _context.ChangeTracker.Clear();
        return result;
    }

    private async Task<RouteCustomerEntity> FindAsync(string route, string code)
        => await _context.RouteCustomers
            .AsNoTracking()
            .SingleAsync(customer => customer.AssignedBusinessPartnerCode == route && customer.Code == code);

    private static IBusinessPartnerService BusinessPartners() =>
        StubProxy.For<IBusinessPartnerService>((method, _) => method.Name switch
        {
            nameof(IBusinessPartnerService.GetBusinessPartnerByCodeAsync) => Task.FromResult<BusinessPartnerDto?>(
                new BusinessPartnerDto
                {
                    CardCode = RouteCode,
                    CardName = "Van 030",
                    CardType = "L",
                    Currency = "USD"
                }),
            _ => throw new InvalidOperationException($"IBusinessPartnerService.{method.Name} was not expected.")
        });

    private static User NewUser(Guid id, string username, string role, string? assignedBusinessPartnerCode) => new()
    {
        Id = id,
        Username = username,
        Email = $"{username}@example.com",
        PasswordHash = "x",
        Role = role,
        IsActive = true,
        AssignedWarehouseCode = assignedBusinessPartnerCode,
        AssignedCostCentreCode = assignedBusinessPartnerCode is null ? null : "CC030",
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

    /// <summary>Runs the real update, so what the immutable fields end up as is actually asserted.</summary>
    private sealed class RouteCustomerMediator(ApplicationDbContext context) : IMediator
    {
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is UpdateRouteCustomerCommand command)
            {
                var result = await new UpdateRouteCustomerHandler(context).Handle(command, cancellationToken);
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
