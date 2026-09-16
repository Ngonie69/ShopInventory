using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.RouteCustomers;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerProductMix;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomers;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerSalesSummary;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// A vendor is not a route customer.
/// </summary>
/// <remarks>
/// Route customers are the vans' shops: a van drives a round, and the shops on that round are the
/// customers booked against it. A vending vendor sells from a cart out of a depot and is on no round at
/// all — it has a depot, not a route. The two share one table, and until <see cref="RouteCustomerScope"/>
/// every read of it answered with both: the route customer list carried vendors grouped under their
/// depots as if the depot were a route, the van sales report counted vending takings while its own copy
/// said "van sales only", and a vendor's sales page put the depot in a breadcrumb under the word "route".
///
/// So each test here is the same shape from both ends — the route read must not see the vendor, and the
/// vending read must not see the shop — because a filter that only drops rows would pass half of them
/// while quietly returning nothing to vending.
/// </remarks>
public sealed class RouteCustomerVendorScopeTests : IDisposable
{
    private const string VanRoute = "VAN010";
    private const string Depot = "COR006";

    private static readonly DateTime Day = new(2026, 9, 10);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    private int _shopId;
    private int _vendorId;
    private int _reference;

    public RouteCustomerVendorScopeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        // What makes COR006 a depot and VAN010 not one. The van's account is deliberately seeded too:
        // a rule that reads "has a user" rather than "has a cart vendor" would pass every test that
        // only seeded one of them.
        User("graniteside.vending", Depot, "KEFGRC", ApplicationRoles.CartVendor);
        User("van010", VanRoute, "VAN010", ApplicationRoles.Sales);

        _shopId = Customer("TUCK01", "Tuck Shop", VanRoute);
        _vendorId = Customer("VMP001", "Tendai", Depot);

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── The list ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_route_customer_list_leaves_the_vendors_out()
    {
        var customers = await ListAsync(RouteCustomerScope.Route);

        Assert.Equal("TUCK01", Assert.Single(customers).Code);
    }

    [Fact]
    public async Task The_vending_list_leaves_the_vans_shops_out()
    {
        var customers = await ListAsync(RouteCustomerScope.Vending);

        Assert.Equal("VMP001", Assert.Single(customers).Code);
    }

    [Fact]
    public async Task Asking_for_all_of_them_still_answers_with_both()
    {
        var customers = await ListAsync(RouteCustomerScope.All);

        Assert.Equal(["TUCK01", "VMP001"], customers.Select(customer => customer.Code).Order());
    }

    /// <summary>
    /// The default is the route, because "route customer" is what the endpoint and every page on it
    /// say. A caller that says nothing is asking about the vans.
    /// </summary>
    [Fact]
    public async Task A_caller_that_names_no_scope_gets_the_routes()
    {
        var result = await new GetRouteCustomersHandler(_context)
            .Handle(new GetRouteCustomersQuery(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("TUCK01", Assert.Single(result.Value).Code);
    }

    /// <summary>
    /// Naming the depot does not get the vendors back under the route scope. The partner filter says
    /// which rows, the scope says which population, and the narrower of the two wins — otherwise the
    /// route sales report's route picker would be a way back into the vending book.
    /// </summary>
    [Fact]
    public async Task Naming_a_depot_under_the_route_scope_answers_with_nothing()
    {
        var result = await new GetRouteCustomersHandler(_context)
            .Handle(new GetRouteCustomersQuery(Depot, true, RouteCustomerScope.Route), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Empty(result.Value);
    }

    // ── The sales summary ──────────────────────────────────────────────────

    [Fact]
    public async Task The_route_sales_report_does_not_count_vending_takings()
    {
        Sell(_shopId, "TUCK01", VanRoute, 100m);
        Sell(_vendorId, "VMP001", Depot, 250m);
        await _context.SaveChangesAsync();

        var summary = await SummaryAsync(RouteCustomerScope.Route);

        var route = Assert.Single(summary.Routes);
        Assert.Equal(VanRoute, route.AssignedBusinessPartnerCode);
        Assert.Equal(100m, route.TotalsByCurrency.Single().Gross);
    }

    [Fact]
    public async Task The_vending_summary_does_not_count_the_vans_takings()
    {
        Sell(_shopId, "TUCK01", VanRoute, 100m);
        Sell(_vendorId, "VMP001", Depot, 250m);
        await _context.SaveChangesAsync();

        var summary = await SummaryAsync(RouteCustomerScope.Vending);

        var depot = Assert.Single(summary.Routes);
        Assert.Equal(Depot, depot.AssignedBusinessPartnerCode);
        Assert.Equal(250m, depot.TotalsByCurrency.Single().Gross);
    }

    /// <summary>
    /// A sale whose customer record has since been removed keeps only the code and the partner it was
    /// booked against. It is still that partner's takings, so it has to be filed by the same rule —
    /// the summary's orphan rows are where a scope applied to the customer table alone would leak.
    /// </summary>
    [Fact]
    public async Task A_deleted_vendors_sale_is_still_vending_takings()
    {
        Sell(routeCustomerId: null, "VMP404", Depot, 40m);
        await _context.SaveChangesAsync();

        // The van's own shop is still reported — it simply bought nothing — and the depot is not there
        // at all, neither as a group of its own nor as takings folded into the van's.
        var routes = await SummaryAsync(RouteCustomerScope.Route);
        Assert.DoesNotContain(routes.Routes, group => group.AssignedBusinessPartnerCode == Depot);
        Assert.All(routes.Routes, group => Assert.Empty(group.TotalsByCurrency));

        var depot = Assert.Single((await SummaryAsync(RouteCustomerScope.Vending)).Routes);
        Assert.Equal(Depot, depot.AssignedBusinessPartnerCode);
        Assert.Contains(depot.Customers, row => row.Code == "VMP404" && row.SaleCount == 1);
        Assert.Equal(40m, depot.TotalsByCurrency.Single().Gross);
    }

    // ── The product mix ────────────────────────────────────────────────────

    [Fact]
    public async Task The_product_mix_across_every_route_leaves_the_vending_lines_out()
    {
        Sell(_shopId, "TUCK01", VanRoute, 100m, item: "CHE011");
        Sell(_vendorId, "VMP001", Depot, 250m, item: "FRZ004");
        await _context.SaveChangesAsync();

        var route = await ProductMixAsync(RouteCustomerScope.Route);
        Assert.Equal("CHE011", Assert.Single(route.Items).ItemCode);

        var vending = await ProductMixAsync(RouteCustomerScope.Vending);
        Assert.Equal("FRZ004", Assert.Single(vending.Items).ItemCode);
    }

    /// <summary>
    /// A named customer is already whichever population it belongs to, so its own mix is not filtered
    /// out from under it. This is the read the vendor's sales page makes.
    /// </summary>
    [Fact]
    public async Task One_vendors_product_mix_is_still_answered_under_either_scope()
    {
        Sell(_vendorId, "VMP001", Depot, 250m, item: "FRZ004");
        await _context.SaveChangesAsync();

        foreach (var scope in new[] { RouteCustomerScope.Route, RouteCustomerScope.Vending })
        {
            var result = await new GetRouteCustomerProductMixHandler(_context).Handle(
                new GetRouteCustomerProductMixQuery(null, _vendorId, null, null, 0, scope),
                CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Equal("FRZ004", Assert.Single(result.Value.Items).ItemCode);
        }
    }

    // ── Fixtures ───────────────────────────────────────────────────────────

    private async Task<List<ShopInventory.DTOs.RouteCustomerDto>> ListAsync(RouteCustomerScope scope)
    {
        var result = await new GetRouteCustomersHandler(_context)
            .Handle(new GetRouteCustomersQuery(null, false, scope), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private async Task<ShopInventory.DTOs.RouteCustomerSalesSummaryDto> SummaryAsync(RouteCustomerScope scope)
    {
        _context.ChangeTracker.Clear();
        var result = await new GetRouteCustomerSalesSummaryHandler(_context).Handle(
            new GetRouteCustomerSalesSummaryQuery(null, Day.AddDays(-30), Day, null, true, scope),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private async Task<ShopInventory.DTOs.RouteCustomerProductMixDto> ProductMixAsync(RouteCustomerScope scope)
    {
        _context.ChangeTracker.Clear();
        var result = await new GetRouteCustomerProductMixHandler(_context).Handle(
            new GetRouteCustomerProductMixQuery(null, null, Day.AddDays(-30), Day, 0, scope),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private void User(string username, string businessPartnerCode, string warehouse, string role) =>
        _context.Users.Add(new ShopInventory.Models.User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = "x",
            Role = role,
            IsActive = true,
            AssignedBusinessPartnerCode = businessPartnerCode,
            AssignedCostCentreCode = "CC",
            AssignedWarehouseCodes = JsonSerializer.Serialize(new[] { warehouse }),
        });

    private int Customer(string code, string name, string businessPartnerCode)
    {
        var customer = new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = businessPartnerCode,
            Code = code,
            Name = name,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };

        _context.RouteCustomers.Add(customer);
        _context.SaveChanges();
        return customer.Id;
    }

    private void Sell(
        int? routeCustomerId,
        string routeCustomerCode,
        string cardCode,
        decimal total,
        string item = "CHE011")
    {
        var reference = $"SALE-{++_reference:000}";

        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            DocDate = Day,
            CardCode = cardCode,
            RouteCustomerId = routeCustomerId,
            RouteCustomerCode = routeCustomerCode,
            Currency = "USD",
            TotalAmount = total,
            VatAmount = 0m,
            SourceSystem = SaleSourceSystems.Vending,
            CreatedAt = DateTime.UtcNow,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    ItemCode = item,
                    ItemDescription = item,
                    Quantity = 1m,
                    UnitPrice = total,
                    LineTotal = total,
                    WarehouseCode = "KEFGRC",
                },
            ],
        });
    }
}
