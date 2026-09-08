using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.RouteCustomers.Commands.CreateRouteCustomer;
using ShopInventory.Features.RouteCustomers.Commands.UpdateRouteCustomer;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomers;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// A route customer is a shop on a van's route or a cart vendor standing at a counter, and the two
/// are named differently: a shop has one name, a vendor has a first name and a surname.
/// </summary>
/// <remarks>
/// The surname is a column beside <c>Name</c> rather than a narrowing of it, so the case these cover
/// is really one rule — that a shop is unaffected. Every row that existed before vendors did has a
/// null surname, every handset still reads its whole name out of <c>Name</c>, and nothing had to be
/// backfilled for that to be true.
/// </remarks>
public sealed class RouteCustomerSurnameTests : IDisposable
{
    private const string RouteCode = "CIS006";
    private static readonly Guid Cashier = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public RouteCustomerSurnameTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = Cashier,
            Username = "machipisa.vendors",
            Email = "machipisa.vendors@example.com",
            PasswordHash = "x",
            Role = "CartVendor",
            IsActive = true,
            AssignedWarehouseCode = "CORMACH2",
            AssignedBusinessPartnerCode = RouteCode
        });

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_vendor_keeps_the_surname_it_was_captured_with()
    {
        var created = await CreateAsync(new CreateRouteCustomerRequest
        {
            Code = "TAPIWA1",
            Name = "Tapiwa",
            Surname = "Moyo",
            Phone = "0771234567"
        });

        Assert.Equal("Tapiwa", created.Name);
        Assert.Equal("Moyo", created.Surname);

        var stored = await _context.RouteCustomers.AsNoTracking().SingleAsync();
        Assert.Equal("Moyo", stored.Surname);
    }

    [Fact]
    public async Task The_surname_reaches_the_list_the_till_reads()
    {
        await CreateAsync(new CreateRouteCustomerRequest
        {
            Code = "TAPIWA1",
            Name = "Tapiwa",
            Surname = "Moyo"
        });

        var listed = Assert.Single(await ListAsync());
        Assert.Equal("Moyo", listed.Surname);
    }

    /// <summary>
    /// The case the column exists for: a shop is captured with no surname and reads back with none,
    /// rather than with an empty string standing in for one.
    /// </summary>
    [Fact]
    public async Task A_shop_captured_without_a_surname_has_none()
    {
        var created = await CreateAsync(new CreateRouteCustomerRequest
        {
            Code = "TUCK01",
            Name = "Tuck Shop"
        });

        Assert.Null(created.Surname);
        Assert.Null(Assert.Single(await ListAsync()).Surname);
    }

    /// <summary>
    /// Blank is not a surname. The handler trims every other optional field to null and this one
    /// has to agree, or a vendor captured with a stray space sorts and displays differently from one
    /// captured without.
    /// </summary>
    [Fact]
    public async Task A_blank_surname_is_stored_as_no_surname()
    {
        var created = await CreateAsync(new CreateRouteCustomerRequest
        {
            Code = "TUCK02",
            Name = "Tuck Shop",
            Surname = "   "
        });

        Assert.Null(created.Surname);
    }

    [Fact]
    public async Task A_surname_can_be_corrected_afterwards()
    {
        var created = await CreateAsync(new CreateRouteCustomerRequest
        {
            Code = "TAPIWA1",
            Name = "Tapiwa",
            Surname = "Moya"
        });

        var updated = await UpdateAsync(created.Id, new UpdateRouteCustomerRequest
        {
            AssignedBusinessPartnerCode = RouteCode,
            Code = created.Code,
            Name = "Tapiwa",
            Surname = "Moyo",
            IsActive = true
        });

        Assert.Equal("Moyo", updated.Surname);
    }

    /// <summary>
    /// Editing a shop that never had one must not invent one, which is what a non-null default on
    /// the request would have done the first time anybody saved an untouched row.
    /// </summary>
    [Fact]
    public async Task Editing_a_shop_leaves_it_without_a_surname()
    {
        var created = await CreateAsync(new CreateRouteCustomerRequest
        {
            Code = "TUCK01",
            Name = "Tuck Shop"
        });

        var updated = await UpdateAsync(created.Id, new UpdateRouteCustomerRequest
        {
            AssignedBusinessPartnerCode = RouteCode,
            Code = created.Code,
            Name = "Tuck Shop Extended",
            IsActive = true
        });

        Assert.Null(updated.Surname);
    }

    private async Task<RouteCustomerDto> CreateAsync(CreateRouteCustomerRequest request)
    {
        var result = await new CreateRouteCustomerHandler(
                _context, new NoOpNotificationService(), NullLogger<CreateRouteCustomerHandler>.Instance)
            .Handle(new CreateRouteCustomerCommand(request, Cashier), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        _context.ChangeTracker.Clear();
        return result.Value;
    }

    private async Task<RouteCustomerDto> UpdateAsync(int id, UpdateRouteCustomerRequest request)
    {
        var result = await new UpdateRouteCustomerHandler(_context)
            .Handle(new UpdateRouteCustomerCommand(id, request), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        _context.ChangeTracker.Clear();
        return result.Value;
    }

    private async Task<List<RouteCustomerDto>> ListAsync()
    {
        var result = await new GetRouteCustomersHandler(_context)
            .Handle(new GetRouteCustomersQuery(RouteCode, true), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }
}
