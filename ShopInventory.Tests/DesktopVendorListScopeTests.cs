using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetVendorsForAccount;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The vendor list a till reads is scoped by the server, not by the till.
/// </summary>
/// <remarks>
/// <c>GET /api/route-customers</c> filters on a business partner the caller supplies and returns
/// every route's customers when none is given. A till pointed at that endpoint would be trusted to
/// pass its own code correctly, for ever — against a list it could just as easily ask for in full.
///
/// So the till reads this instead, which names no partner and takes one off the account through
/// <c>SellingAccountResolver</c>: the same value <c>CreateDesktopSale</c> resolves the vendor
/// against. These pin the two properties that follow from that — a cashier sees their own shop's
/// vendors and only those, and the list never offers a vendor the sale would then refuse.
/// </remarks>
public sealed class DesktopVendorListScopeTests : IDisposable
{
    private const string MachipisaPartner = "CIS006";
    private const string GranitesidePartner = "GRS001";

    private static readonly Guid MachipisaCashier = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GranitesideCashier = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public DesktopVendorListScopeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        var machipisa = NewShop("MACHIPISA", "Machipisa", MachipisaPartner, "CORMACH2");
        var graniteside = NewShop("GRANITESIDE", "Graniteside", GranitesidePartner, "KEFGRS");
        _context.Shops.AddRange(machipisa, graniteside);
        _context.SaveChanges();

        // Created the way the console creates one: a role and a shop, and no business partner on the
        // account's own columns. That is exactly the case a list keyed off those columns would miss.
        _context.Users.AddRange(
            NewCashier(MachipisaCashier, "machipisa.vendors", machipisa.Id),
            NewCashier(GranitesideCashier, "graniteside.vendors", graniteside.Id));

        _context.RouteCustomers.AddRange(
            NewVendor(MachipisaPartner, "TAPIWA1", "Tapiwa", "Moyo"),
            NewVendor(MachipisaPartner, "RUTH1", "Ruth", "Banda"),
            NewVendor(MachipisaPartner, "GONE1", "Departed", "Vendor", isActive: false),
            NewVendor(GranitesidePartner, "SIBO1", "Sibongile", "Ncube"));

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_cashier_sees_the_vendors_stationed_at_their_own_shop()
    {
        var vendors = await ListAsync(MachipisaCashier);

        Assert.Equal(["RUTH1", "TAPIWA1"], vendors.Select(vendor => vendor.Code).OrderBy(code => code));
    }

    /// <summary>
    /// The separation the whole design rests on: location is the business partner, and a cashier
    /// cannot reach another location's vendors because the request never names one.
    /// </summary>
    [Fact]
    public async Task A_cashier_never_sees_another_shops_vendors()
    {
        var machipisa = await ListAsync(MachipisaCashier);
        var graniteside = await ListAsync(GranitesideCashier);

        Assert.DoesNotContain(machipisa, vendor => vendor.Code == "SIBO1");
        Assert.Equal("SIBO1", Assert.Single(graniteside).Code);
    }

    /// <summary>
    /// A removed vendor is off the list because the sale would refuse it. The two filters have to
    /// agree, or the operator is offered a name the server then rejects.
    /// </summary>
    [Fact]
    public async Task A_deactivated_vendor_is_not_offered()
    {
        var vendors = await ListAsync(MachipisaCashier);

        Assert.DoesNotContain(vendors, vendor => vendor.Code == "GONE1");
    }

    [Fact]
    public async Task The_list_carries_what_the_counter_has_to_show()
    {
        var tapiwa = (await ListAsync(MachipisaCashier)).Single(vendor => vendor.Code == "TAPIWA1");

        Assert.Equal("Tapiwa", tapiwa.Name);
        Assert.Equal("Moyo", tapiwa.Surname);
    }

    /// <summary>People are listed by family name, which is how a queue of them is called.</summary>
    [Fact]
    public async Task Vendors_are_listed_by_surname()
    {
        var vendors = await ListAsync(MachipisaCashier);

        Assert.Equal(["Banda", "Moyo"], vendors.Select(vendor => vendor.Surname));
    }

    [Fact]
    public async Task An_account_that_cannot_sell_is_refused_rather_than_given_an_empty_list()
    {
        var stranded = Guid.Parse("33333333-3333-3333-3333-333333333333");
        _context.Users.Add(new User
        {
            Id = stranded,
            Username = "no.shop",
            Email = "no.shop@example.com",
            PasswordHash = "x",
            Role = "CartVendor",
            IsActive = true
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await new GetVendorsForAccountHandler(_context)
            .Handle(new GetVendorsForAccountQuery(stranded), CancellationToken.None);

        // An empty list would read as "this shop has no vendors yet", which is a different and much
        // more confusing thing than an account nobody has finished setting up.
        Assert.True(result.IsError);
    }

    private async Task<List<DTOs.DesktopVendorDto>> ListAsync(Guid userId)
    {
        var result = await new GetVendorsForAccountHandler(_context)
            .Handle(new GetVendorsForAccountQuery(userId), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private static ShopEntity NewShop(string code, string name, string partner, string warehouse) => new()
    {
        Code = code,
        Name = name,
        BusinessPartnerCode = partner,
        WarehouseCode = warehouse,
        IsActive = true
    };

    private static User NewCashier(Guid id, string username, int shopId) => new()
    {
        Id = id,
        Username = username,
        Email = $"{username}@example.com",
        PasswordHash = "x",
        Role = "CartVendor",
        IsActive = true,
        ShopId = shopId
    };

    private static RouteCustomerEntity NewVendor(
        string partner, string code, string name, string surname, bool isActive = true) => new()
    {
        AssignedBusinessPartnerCode = partner,
        Code = code,
        Name = name,
        Surname = surname,
        IsActive = isActive,
        CreatedAt = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc)
    };
}
