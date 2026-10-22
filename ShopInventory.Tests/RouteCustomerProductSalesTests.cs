using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.RouteCustomers;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerProductSales;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerSalesSummary;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The read behind the Depots &amp; vendors workbook: what each vendor bought of each item.
/// </summary>
/// <remarks>
/// The workbook puts these rows beside the sales summary's vendor totals, so the tests that matter most
/// are the ones where the two reads could disagree — whose sale it was, and whether the money adds up.
/// </remarks>
public sealed class RouteCustomerProductSalesTests : IDisposable
{
    private const string VanRoute = "VAN010";
    private const string Depot = "COR006";
    private const string OtherDepot = "COR008";

    private static readonly DateTime Day = new(2026, 10, 10);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    private readonly int _shopId;
    private readonly int _tendaiId;
    private readonly int _rudoId;
    private readonly int _farai;
    private int _reference;

    public RouteCustomerProductSalesTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        User("graniteside.vending", Depot, "KEFGRC", ApplicationRoles.CartVendor);
        User("bulawayo.vending", OtherDepot, "KEFBYC", ApplicationRoles.CartVendor);
        User("van010", VanRoute, "VAN010", ApplicationRoles.Sales);

        _shopId = Customer("TUCK01", "Tuck Shop", VanRoute);
        _tendaiId = Customer("VMP001", "Tendai", Depot);
        _rudoId = Customer("VMP002", "Rudo", Depot);
        _farai = Customer("VMB001", "Farai", OtherDepot);

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Each_vendor_keeps_its_own_row_for_an_item_they_both_sell()
    {
        Sell(_tendaiId, "VMP001", Depot, ("CHE011", 2m, 3.00m));
        Sell(_rudoId, "VMP002", Depot, ("CHE011", 5m, 3.00m));
        await _context.SaveChangesAsync();

        var rows = await ProductSalesAsync(RouteCustomerScope.Vending);

        Assert.Equal(2, rows.Count);
        var tendai = Assert.Single(rows, row => row.RouteCustomerId == _tendaiId);
        var rudo = Assert.Single(rows, row => row.RouteCustomerId == _rudoId);
        Assert.Equal((2m, 6.00m), (tendai.Quantity, tendai.LineTotal));
        Assert.Equal((5m, 15.00m), (rudo.Quantity, rudo.LineTotal));
        Assert.All(rows, row => Assert.Equal(Depot, row.AssignedBusinessPartnerCode));
    }

    [Fact]
    public async Task One_vendors_sales_of_an_item_fold_into_one_row()
    {
        Sell(_tendaiId, "VMP001", Depot, ("CHE011", 2m, 3.00m), ("SNK002", 1m, 1.50m));
        Sell(_tendaiId, "VMP001", Depot, ("CHE011", 4m, 3.00m));
        await _context.SaveChangesAsync();

        var rows = await ProductSalesAsync(RouteCustomerScope.Vending);

        var cheese = Assert.Single(rows, row => row.ItemCode == "CHE011");
        Assert.Equal(6m, cheese.Quantity);
        Assert.Equal(18.00m, cheese.LineTotal);
        Assert.Equal(2, cheese.LineCount);
        Assert.Single(rows, row => row.ItemCode == "SNK002");
    }

    /// <summary>
    /// A sale written before the vendor id was stamped carries only the code. The summary files it under
    /// the live vendor with that code at that depot, so this read must too, or the workbook would show
    /// the vendor's money on one row and their items on another.
    /// </summary>
    [Fact]
    public async Task A_sale_with_only_a_code_is_filed_under_the_live_vendor_at_that_depot()
    {
        Sell(null, "vmp002", Depot, ("CHE011", 1m, 3.00m));
        await _context.SaveChangesAsync();

        var row = Assert.Single(await ProductSalesAsync(RouteCustomerScope.Vending));

        Assert.Equal(_rudoId, row.RouteCustomerId);
    }

    [Fact]
    public async Task A_code_with_no_live_vendor_is_kept_with_no_id_rather_than_dropped()
    {
        Sell(null, "VMP404", Depot, ("CHE011", 1m, 3.00m));
        await _context.SaveChangesAsync();

        var row = Assert.Single(await ProductSalesAsync(RouteCustomerScope.Vending));

        Assert.Null(row.RouteCustomerId);
        Assert.Equal("VMP404", row.RouteCustomerCode);
    }

    [Fact]
    public async Task The_vending_read_leaves_the_vans_out_and_the_route_read_leaves_vending_out()
    {
        Sell(_shopId, "TUCK01", VanRoute, ("CHE011", 1m, 3.00m));
        Sell(_tendaiId, "VMP001", Depot, ("CHE011", 1m, 3.00m));
        await _context.SaveChangesAsync();

        Assert.Equal(_tendaiId, Assert.Single(await ProductSalesAsync(RouteCustomerScope.Vending)).RouteCustomerId);
        Assert.Equal(_shopId, Assert.Single(await ProductSalesAsync(RouteCustomerScope.Route)).RouteCustomerId);
    }

    [Fact]
    public async Task Naming_a_depot_answers_with_that_depot_alone()
    {
        Sell(_tendaiId, "VMP001", Depot, ("CHE011", 1m, 3.00m));
        Sell(_farai, "VMB001", OtherDepot, ("CHE011", 1m, 3.00m));
        await _context.SaveChangesAsync();

        var result = await new GetRouteCustomerProductSalesHandler(_context).Handle(
            new GetRouteCustomerProductSalesQuery(OtherDepot, Day, Day, RouteCustomerScope.Vending),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(_farai, Assert.Single(result.Value.Rows).RouteCustomerId);
    }

    [Fact]
    public async Task Sales_outside_the_window_are_not_counted()
    {
        Sell(_tendaiId, "VMP001", Depot, ("CHE011", 1m, 3.00m));
        Sell(_tendaiId, "VMP001", Depot, Day.AddDays(-1), ("CHE011", 9m, 3.00m));
        await _context.SaveChangesAsync();

        var row = Assert.Single(await ProductSalesAsync(RouteCustomerScope.Vending));

        Assert.Equal(1m, row.Quantity);
    }

    /// <summary>
    /// An online van sale leaves a copy of its cart on the till table for the receipt; its lines are read
    /// from the reservation. Reading both doubles it.
    /// </summary>
    [Fact]
    public async Task The_receipt_copy_of_an_online_van_sale_is_not_counted_twice()
    {
        Sell(_shopId, "TUCK01", VanRoute, Day, SaleSourceSystems.VanSalesOnline, ("CHE011", 1m, 3.00m));
        await _context.SaveChangesAsync();

        Assert.Empty(await ProductSalesAsync(RouteCustomerScope.Route));
    }

    /// <summary>
    /// The reconciliation the workbook states: a vending till prices lines before VAT, so a vendor's
    /// item values add up to their sales less VAT, to the cent.
    /// </summary>
    [Fact]
    public async Task A_vendors_item_values_add_up_to_their_sales_less_vat()
    {
        Sell(_tendaiId, "VMP001", Depot, ("CHE011", 3m, 2.17m), ("SNK002", 1m, 0.87m));
        Sell(_tendaiId, "VMP001", Depot, ("CHE011", 1m, 2.17m));
        await _context.SaveChangesAsync();

        var rows = await ProductSalesAsync(RouteCustomerScope.Vending);

        var summary = await new GetRouteCustomerSalesSummaryHandler(_context).Handle(
            new GetRouteCustomerSalesSummaryQuery(Depot, Day, Day, null, true, RouteCustomerScope.Vending),
            CancellationToken.None);
        var vendor = summary.Value.Routes.Single().Customers.Single(customer => customer.RouteCustomerId == _tendaiId);
        var totals = vendor.TotalsByCurrency.Single();

        Assert.True(totals.Vat > 0m);
        Assert.Equal(totals.Gross - totals.Vat, rows.Sum(row => row.LineTotal));
    }

    private async Task<List<RouteCustomerProductSalesRowDto>> ProductSalesAsync(RouteCustomerScope scope)
    {
        var result = await new GetRouteCustomerProductSalesHandler(_context).Handle(
            new GetRouteCustomerProductSalesQuery(null, Day, Day, scope),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal(Day, result.Value.From);
        return result.Value.Rows;
    }

    private void User(string username, string businessPartnerCode, string warehouse, string role) =>
        _context.Users.Add(new User
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

    private void Sell(int? routeCustomerId, string code, string cardCode, params (string Item, decimal Quantity, decimal Price)[] lines) =>
        Sell(routeCustomerId, code, cardCode, Day, SaleSourceSystems.Vending, lines);

    private void Sell(int? routeCustomerId, string code, string cardCode, DateTime day, params (string Item, decimal Quantity, decimal Price)[] lines) =>
        Sell(routeCustomerId, code, cardCode, day, SaleSourceSystems.Vending, lines);

    /// <summary>A till sale as CreateDesktopSale writes one: lines before VAT, the tax added to the total.</summary>
    private void Sell(
        int? routeCustomerId,
        string code,
        string cardCode,
        DateTime day,
        string source,
        params (string Item, decimal Quantity, decimal Price)[] lines)
    {
        var entities = lines.Select((line, index) => new DesktopSaleLineEntity
        {
            LineNum = index + 1,
            ItemCode = line.Item,
            ItemDescription = line.Item,
            Quantity = line.Quantity,
            UnitPrice = line.Price,
            LineTotal = Math.Round(line.Quantity * line.Price, 2),
            WarehouseCode = "KEFGRC",
        }).ToList();

        var subtotal = entities.Sum(line => line.LineTotal);
        var vat = Math.Round(subtotal * 0.155m, 2);

        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = $"SALE-{++_reference:000}",
            DocDate = day,
            CardCode = cardCode,
            RouteCustomerId = routeCustomerId,
            RouteCustomerCode = code,
            Currency = "USD",
            TotalAmount = subtotal + vat,
            VatAmount = vat,
            SourceSystem = source,
            CreatedAt = DateTime.UtcNow,
            Lines = entities,
        });
    }
}
