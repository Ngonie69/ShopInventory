using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.Vending.Queries.GetVendingOverview;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The vending page's read: depots, the cashier accounts at them and the vendors they serve.
///
/// A depot is the business partner its cashiers sell on. There are four, each with its own partner and
/// vendors, and a depot may have several cashiers — so the checks that matter are that a vendor lands
/// under its own depot only, and that cashiers at one depot who disagree on warehouse or cost centre are
/// called out rather than silently averaged.
/// </summary>
public sealed class VendingOverviewTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VendingOverviewTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
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

    private void Cashier(string username, string? bp, string? warehouse, string? costCentre = "CC-1",
        bool isActive = true, string role = ApplicationRoles.CartVendor)
        => _context.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = "x",
            Role = role,
            IsActive = isActive,
            AssignedBusinessPartnerCode = bp,
            AssignedCostCentreCode = costCentre,
            AssignedWarehouseCodes = warehouse is null ? null : JsonSerializer.Serialize(new[] { warehouse }),
        });

    private void Vendor(string code, string bp, bool isActive = true)
        => _context.RouteCustomers.Add(new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = bp,
            Code = code,
            Name = $"Vendor {code}",
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow,
        });

    private async Task<ShopInventory.DTOs.VendingOverviewDto> ReadAsync()
    {
        await _context.SaveChangesAsync();
        var result = await new GetVendingOverviewHandler(_context)
            .Handle(new GetVendingOverviewQuery(), CancellationToken.None);
        Assert.False(result.IsError);
        return result.Value;
    }

    [Fact]
    public async Task Each_depot_lists_only_its_own_vendors_and_cashiers()
    {
        Cashier("depot1-a", "DEPOT1", "WH1");
        Cashier("depot1-b", "DEPOT1", "WH1");
        Cashier("depot2-a", "DEPOT2", "WH2");
        Vendor("V1", "DEPOT1");
        Vendor("V2", "DEPOT1", isActive: false);
        Vendor("V3", "DEPOT2");

        var overview = await ReadAsync();

        Assert.Equal(["DEPOT1", "DEPOT2"], overview.Depots.Select(depot => depot.BusinessPartnerCode));

        var depot1 = overview.Depots[0];
        Assert.Equal(2, depot1.CashierCount);
        Assert.Equal(2, depot1.VendorCount);
        Assert.Equal(1, depot1.ActiveVendorCount);
        Assert.Equal(["WH1"], depot1.WarehouseCodes);
        Assert.Null(depot1.SetupProblem);

        Assert.Equal(1, overview.Depots[1].VendorCount);
        Assert.All(overview.Accounts.Where(a => a.BusinessPartnerCode == "DEPOT1"), a => Assert.Equal(1, a.ActiveVendorCount));
    }

    [Fact]
    public async Task A_van_route_and_its_shops_are_not_vending()
    {
        // Vendors share the route customer table with the vans' shops. Only a partner a vending account
        // sells on makes its rows vendors.
        Cashier("depot1-a", "DEPOT1", "WH1");
        Cashier("van-1", "VAN-BP", "VAN001", role: ApplicationRoles.Sales);
        Vendor("V1", "DEPOT1");
        Vendor("SHOP-ON-A-ROUTE", "VAN-BP");

        var overview = await ReadAsync();

        Assert.Single(overview.Accounts);
        Assert.Equal(["V1"], overview.Vendors.Select(vendor => vendor.Code));
    }

    [Fact]
    public async Task Cashiers_at_one_depot_drawing_from_different_warehouses_are_flagged()
    {
        Cashier("depot1-a", "DEPOT1", "WH1");
        Cashier("depot1-b", "DEPOT1", "WH9");

        var depot = Assert.Single((await ReadAsync()).Depots);

        Assert.Equal(["WH1", "WH9"], depot.WarehouseCodes);
        Assert.Contains("2 different warehouses", depot.SetupProblem);
    }

    [Fact]
    public async Task A_closed_account_on_an_old_warehouse_is_not_a_disagreement()
    {
        Cashier("depot1-a", "DEPOT1", "WH1");
        Cashier("depot1-old", "DEPOT1", "WH-OLD", isActive: false);

        var depot = Assert.Single((await ReadAsync()).Depots);

        Assert.Equal(["WH1"], depot.WarehouseCodes);
        Assert.Null(depot.SetupProblem);
        Assert.Equal(1, depot.ActiveCashierCount);
    }

    [Fact]
    public async Task An_account_the_till_would_refuse_says_why()
    {
        Cashier("no-warehouse", "DEPOT1", warehouse: null);
        Cashier("no-cost-centre", "DEPOT1", "WH1", costCentre: null);
        Cashier("ready", "DEPOT1", "WH1");

        var accounts = (await ReadAsync()).Accounts.ToDictionary(account => account.Username);

        Assert.NotNull(accounts["no-warehouse"].SetupProblem);
        Assert.Contains("cost centre", accounts["no-cost-centre"].SetupProblem);
        Assert.Null(accounts["ready"].SetupProblem);
    }
}
