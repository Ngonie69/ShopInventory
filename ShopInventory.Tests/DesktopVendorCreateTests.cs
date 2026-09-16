using System.Text.Json;
using ErrorOr;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.CreateVendorForAccount;
using ShopInventory.Features.RouteCustomers.Commands.CreateRouteCustomer;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// A cart-vendor till adds a vendor at its own depot, under the rules the Vending page is held to.
/// </summary>
/// <remarks>
/// The account lacks <c>customers.create</c> on purpose — that permission opens routes which name a depot
/// in the request — so this route takes the depot off the account instead. These pin both halves: the
/// convention (<c>VendorCodeConvention</c>) still decides the code, and the till cannot choose where the
/// vendor goes.
/// </remarks>
public sealed class DesktopVendorCreateTests : IDisposable
{
    private const string MachipisaDepot = "CIS006";
    private const string BulawayoDepot = "BYC001";

    private static readonly Guid MachipisaTill = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BulawayoTill = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ShopTill = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid UnmappedTill = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly NoOpNotificationService _notifications = new();

    public DesktopVendorCreateTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.AddRange(
            Account(MachipisaTill, "machipisa.vending", ApplicationRoles.CartVendor, MachipisaDepot, "CORMACH"),
            Account(BulawayoTill, "bulawayo.vending", ApplicationRoles.CartVendor, BulawayoDepot, "KEFBYC"),
            // Sells on a business partner with a warehouse no vendor prefix exists for.
            Account(UnmappedTill, "farm.vending", ApplicationRoles.CartVendor, "FRM001", "KEFSHOP"),
            // A shop till on the Machipisa partner: it sells, but keeps no vendor list.
            Account(ShopTill, "machipisa.shop", ApplicationRoles.Cashier, MachipisaDepot, "CORMACH"));

        _context.RouteCustomers.AddRange(
            Vendor(MachipisaDepot, "VMM126", "Ezra", "Chasakara", "0776875670"),
            Vendor(MachipisaDepot, "VMM127", "Cephas", "Chavhunduka", "0771954751"),
            Vendor(MachipisaDepot, "VMM090", "Departed", "Vendor", "0770000000", isActive: false),
            Vendor(BulawayoDepot, "VMB004", "Sibongile", "Ncube", "0712345678"));

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_vendor_added_without_a_code_takes_the_depots_next_number()
    {
        var created = await AddAsync(MachipisaTill, new CreateDesktopVendorRequest
        {
            Name = "Godkows",
            Surname = "Chida",
            Phone = "0774955069"
        });

        Assert.Equal("VMM128", created.Code);
        Assert.Equal("Godkows", created.Name);
        Assert.Equal("Chida", created.Surname);
    }

    /// <summary>The till never names a depot, so the vendor lands on the one the account sells on.</summary>
    [Fact]
    public async Task The_vendor_is_listed_at_the_accounts_own_depot()
    {
        var created = await AddAsync(BulawayoTill, new CreateDesktopVendorRequest { Name = "Nomsa", Surname = "Dube" });

        var stored = await _context.RouteCustomers.AsNoTracking().SingleAsync(vendor => vendor.Code == created.Code);
        Assert.Equal(BulawayoDepot, stored.AssignedBusinessPartnerCode);
        Assert.Equal("VMB005", created.Code);
    }

    [Fact]
    public async Task A_code_that_does_not_fit_the_depots_prefix_is_refused()
    {
        var result = await HandleAsync(MachipisaTill, new CreateDesktopVendorRequest
        {
            Code = "VMB200",
            Name = "Forget",
            Surname = "Chidhakwa"
        });

        Assert.Equal("Vending.VendorCodeDoesNotFitDepot", Assert.Single(result.Errors).Code);
        Assert.False(await _context.RouteCustomers.AnyAsync(vendor => vendor.Code == "VMB200"));
    }

    [Theory]
    [InlineData("VMM1")]
    [InlineData("VMM-001")]
    [InlineData("VMM0001")]
    [InlineData("VMM000")]
    public async Task A_code_outside_the_convention_is_refused(string code)
    {
        var result = await HandleAsync(MachipisaTill, new CreateDesktopVendorRequest { Code = code, Name = "Talent" });

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task A_code_held_at_another_depot_is_refused()
    {
        _context.RouteCustomers.Add(Vendor(BulawayoDepot, "VMM300", "Misfiled", "Vendor", null));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await HandleAsync(MachipisaTill, new CreateDesktopVendorRequest { Code = "VMM300", Name = "Talent" });

        Assert.Equal("Vending.VendorCodeTaken", Assert.Single(result.Errors).Code);
    }

    /// <summary>
    /// A removed vendor keeps their row, and giving their code brings that row back — so their earlier
    /// takings stay on the vendor who is trading again rather than on a second copy of them.
    /// </summary>
    [Fact]
    public async Task A_removed_vendor_is_restored_under_their_code_rather_than_added_twice()
    {
        var created = await AddAsync(MachipisaTill, new CreateDesktopVendorRequest
        {
            Code = "VMM090",
            Name = "Returned",
            Surname = "Vendor"
        });

        Assert.Equal("VMM090", created.Code);
        var rows = await _context.RouteCustomers.AsNoTracking().Where(vendor => vendor.Code == "VMM090").ToListAsync();
        Assert.True(Assert.Single(rows).IsActive);
    }

    /// <summary>
    /// The retry case: a till that lost the reply sends the same person again, and a generated code would
    /// otherwise list them a second time.
    /// </summary>
    [Fact]
    public async Task The_same_person_is_not_listed_twice()
    {
        var result = await HandleAsync(MachipisaTill, new CreateDesktopVendorRequest
        {
            Name = " ezra ",
            Surname = "CHASAKARA",
            Phone = "077 687 5670"
        });

        var error = Assert.Single(result.Errors);
        Assert.Equal("Vending.VendorAlreadyListed", error.Code);
        Assert.Contains("VMM126", error.Description);
        Assert.Equal(2, await _context.RouteCustomers.CountAsync(vendor => vendor.AssignedBusinessPartnerCode == MachipisaDepot && vendor.IsActive));
    }

    /// <summary>Two people can share a name. A different phone says they are not one person.</summary>
    [Fact]
    public async Task A_namesake_with_another_phone_is_a_different_vendor()
    {
        var created = await AddAsync(MachipisaTill, new CreateDesktopVendorRequest
        {
            Name = "Ezra",
            Surname = "Chasakara",
            Phone = "0719999999"
        });

        Assert.Equal("VMM128", created.Code);
    }

    [Fact]
    public async Task A_first_name_is_required()
    {
        var result = await HandleAsync(MachipisaTill, new CreateDesktopVendorRequest { Name = "   ", Surname = "Chipere" });

        Assert.Contains(result.Errors, error => error.Code == "RouteCustomers.NameRequired");
    }

    [Fact]
    public async Task A_field_longer_than_its_column_is_refused_before_anything_is_saved()
    {
        var result = await HandleAsync(MachipisaTill, new CreateDesktopVendorRequest
        {
            Name = "Talent",
            Phone = new string('7', CreateVendorForAccountHandler.MaxPhoneLength + 1)
        });

        Assert.Equal("Vending.FieldTooLong", Assert.Single(result.Errors).Code);
        Assert.False(await _context.RouteCustomers.AnyAsync(vendor => vendor.Name == "Talent"));
    }

    [Fact]
    public async Task An_account_that_is_not_a_cart_vendor_cannot_add_one()
    {
        var result = await HandleAsync(ShopTill, new CreateDesktopVendorRequest { Name = "Talent" });

        Assert.Equal(ErrorType.Forbidden, Assert.Single(result.Errors).Type);
    }

    /// <summary>
    /// With no depot prefix to number under, the shared handler would fall back to a code made from the
    /// name — the van routes' rule, and outside the vending convention.
    /// </summary>
    [Fact]
    public async Task A_depot_with_no_vendor_prefix_cannot_add_one()
    {
        var result = await HandleAsync(UnmappedTill, new CreateDesktopVendorRequest { Name = "Talent", Surname = "Chipere" });

        Assert.True(result.IsError);
        Assert.False(await _context.RouteCustomers.AnyAsync(vendor => vendor.Name == "Talent"));
    }

    /// <summary>
    /// A cart vendor that sells through a shop rather than its own business partner column is not a
    /// depot the numbering knows. It is refused in words that send the operator somewhere useful, rather
    /// than with the shared handler's complaint about adding to somebody else's route.
    /// </summary>
    [Fact]
    public async Task A_cart_vendor_selling_through_a_shop_is_told_to_ask_an_administrator()
    {
        var shopVendor = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var shop = new ShopEntity
        {
            Code = "KIOSK",
            Name = "Kiosk",
            BusinessPartnerCode = "KSK001",
            WarehouseCode = "CORMACH",
            IsActive = true
        };
        _context.Shops.Add(shop);
        await _context.SaveChangesAsync();

        _context.Users.Add(new User
        {
            Id = shopVendor,
            Username = "kiosk.vending",
            Email = "kiosk.vending@example.com",
            PasswordHash = "x",
            Role = ApplicationRoles.CartVendor,
            IsActive = true,
            ShopId = shop.Id
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await HandleAsync(shopVendor, new CreateDesktopVendorRequest { Name = "Talent" });

        Assert.Equal("Vending.NotAVendingDepot", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task The_new_vendor_is_on_the_list_the_till_reads_next()
    {
        var created = await AddAsync(MachipisaTill, new CreateDesktopVendorRequest { Name = "Talent", Surname = "Chipere" });

        var list = await new Features.DesktopIntegration.Queries.GetVendorsForAccount.GetVendorsForAccountHandler(_context)
            .Handle(new Features.DesktopIntegration.Queries.GetVendorsForAccount.GetVendorsForAccountQuery(MachipisaTill), CancellationToken.None);

        Assert.Contains(list.Value, vendor => vendor.Code == created.Code);
    }

    private async Task<DesktopVendorDto> AddAsync(Guid userId, CreateDesktopVendorRequest request)
    {
        var result = await HandleAsync(userId, request);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private async Task<ErrorOr<DesktopVendorDto>> HandleAsync(Guid userId, CreateDesktopVendorRequest request)
    {
        var result = await new CreateVendorForAccountHandler(
                _context,
                _notifications,
                NullLogger<CreateRouteCustomerHandler>.Instance,
                NullLogger<CreateVendorForAccountHandler>.Instance)
            .Handle(new CreateVendorForAccountCommand(request, userId), CancellationToken.None);

        _context.ChangeTracker.Clear();
        return result;
    }

    private static User Account(Guid id, string username, string role, string partner, string warehouse) => new()
    {
        Id = id,
        Username = username,
        Email = $"{username}@example.com",
        PasswordHash = "x",
        Role = role,
        IsActive = true,
        AssignedBusinessPartnerCode = partner,
        AssignedWarehouseCodes = JsonSerializer.Serialize(new[] { warehouse })
    };

    private static RouteCustomerEntity Vendor(
        string partner, string code, string name, string surname, string? phone, bool isActive = true) => new()
    {
        AssignedBusinessPartnerCode = partner,
        Code = code,
        Name = name,
        Surname = surname,
        Phone = phone,
        IsActive = isActive,
        CreatedAt = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc)
    };
}
