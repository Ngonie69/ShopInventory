using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries.GetVanDailyTruckReport;
using ShopInventory.Features.VanSalesReports.Queries.GetVanManagementExtras;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The management report's two figures the office used to type by hand: the 5 litre Shamiso columns on
/// every truck-day, and the Friday breakages log. Both are read out of the sale lines and the handsets'
/// market breakage reports, on the same truck-day grain as the daily truck report.
/// </summary>
public sealed class VanManagementExtrasTests : IDisposable
{
    private static readonly Guid Rep = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Loose = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string VanAccount = "VAN009";
    private const string Shamiso = "ICS003";
    private const string Cheddar = "CHE011";

    private static readonly DateTime Monday = new(2026, 11, 16);
    private static readonly DateTime Tuesday = new(2026, 11, 17);
    private static readonly DateTime Friday = new(2026, 11, 20);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RouteEntity _west1;
    private readonly RouteEntity _upc1;

    public VanManagementExtrasTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _west1 = new RouteEntity { Code = "WEST1", Name = "West 1", Territory = "Harare", DailySalesTarget = 2500m };
        _upc1 = new RouteEntity { Code = "UPC1", Name = "UPC 1", Territory = "UPC", DailySalesTarget = 3000m };
        _context.Routes.AddRange(_west1, _upc1);
        _context.SaveChanges();

        _context.Users.AddRange(User(Rep, "west1.one", VanAccount, _west1.Id), User(Loose, "no.route", "VAN099", null));
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Only_the_chosen_products_are_counted_per_truck_per_day()
    {
        AddSale(Rep, Monday, (Shamiso, 4m, 6.29m), (Cheddar, 2m, 5m));
        AddSale(Rep, Monday, (Shamiso, 1m, 6.29m));
        AddSale(Rep, Tuesday, (Shamiso, 3m, 6.29m));
        await _context.SaveChangesAsync();

        var extras = await ExtrasAsync(Shamiso);

        Assert.Equal(2, extras.Products.Count);
        var monday = extras.Products.Single(product => product.TradingDate == Monday);
        Assert.Equal(_west1.Id, monday.RouteId);
        Assert.Equal(Shamiso, monday.ItemCode);
        Assert.Equal(5m, monday.Units);
        Assert.Equal(3m, extras.Products.Single(product => product.TradingDate == Tuesday).Units);
    }

    /// <summary>
    /// The value is the same money as the truck report's sales: VAT included. A day that sold nothing but
    /// the chosen product must come to the truck report's figure for that day, to the cent.
    /// </summary>
    [Fact]
    public async Task Product_value_is_the_same_money_as_the_truck_reports_sales()
    {
        AddSale(Rep, Tuesday, (Shamiso, 3m, 6.29m));
        await _context.SaveChangesAsync();

        var extras = await ExtrasAsync(Shamiso);
        var truck = await new GetVanDailyTruckReportHandler(_context, Options.Create(new TaxSettings()))
            .Handle(new GetVanDailyTruckReportQuery(Monday, Friday), CancellationToken.None);

        var day = truck.Value.Days.Single(row => row.RouteId == _west1.Id && row.TradingDate == Tuesday);

        Assert.True(extras.Products.Single().Value > 3m * 6.29m, "the value is grossed up for VAT");
        Assert.Equal(day.Sales, extras.Products.Single().Value);
    }

    [Fact]
    public async Task A_login_on_no_route_is_counted_apart_and_not_dropped()
    {
        AddSale(Loose, Monday, (Shamiso, 2m, 6.29m));
        await _context.SaveChangesAsync();

        var extras = await ExtrasAsync(Shamiso);

        Assert.Empty(extras.Products);
        Assert.Equal(2m, extras.ProductUnitsOutsideRoutes);
    }

    /// <summary>The day's own record wins: a truck opened on another route that day sold on that route.</summary>
    [Fact]
    public async Task A_day_opened_on_another_route_keeps_its_sales_on_that_route()
    {
        _context.VanRouteDays.Add(new VanRouteDayEntity
        {
            UserId = Rep,
            Username = "west1.one",
            TradingDate = Monday,
            RouteId = _upc1.Id,
            RouteCode = _upc1.Code,
            RouteName = _upc1.Name,
            DepartedAt = DateTime.SpecifyKind(Monday.AddHours(5), DateTimeKind.Utc)
        });
        AddSale(Rep, Monday, (Shamiso, 1m, 6.29m));
        AddSale(Rep, Tuesday, (Shamiso, 1m, 6.29m));
        await _context.SaveChangesAsync();

        var extras = await ExtrasAsync(Shamiso);

        Assert.Equal(_upc1.Id, extras.Products.Single(product => product.TradingDate == Monday).RouteId);
        Assert.Equal(_west1.Id, extras.Products.Single(product => product.TradingDate == Tuesday).RouteId);
    }

    [Fact]
    public async Task No_products_chosen_counts_nothing_and_still_returns_the_breakages()
    {
        AddSale(Rep, Monday, (Shamiso, 1m, 6.29m));
        AddBreakage(Rep, Friday, MarketBreakageStatuses.Pending, (Shamiso, 2m, null, "Crushed"));
        await _context.SaveChangesAsync();

        var extras = await ExtrasAsync();

        Assert.Empty(extras.Products);
        Assert.Single(extras.Breakages);
    }

    /// <summary>
    /// The office's count is the figure once there is one; the rep's report stands until then. A report
    /// the office turned down is not a breakage.
    /// </summary>
    [Fact]
    public async Task Breakages_take_the_offices_count_and_leave_out_rejected_reports()
    {
        AddBreakage(Rep, Friday, MarketBreakageStatuses.Transferred, (Shamiso, 5m, 4m, "Dropped"), (Cheddar, 1m, null, null));
        AddBreakage(Rep, Friday, MarketBreakageStatuses.Rejected, (Shamiso, 9m, null, "Crushed"));
        await _context.SaveChangesAsync();

        var extras = await ExtrasAsync(Shamiso);

        Assert.Equal(2, extras.Breakages.Count);
        var shamiso = extras.Breakages.Single(line => line.ItemCode == Shamiso);
        Assert.Equal(4m, shamiso.Units);
        Assert.True(shamiso.IsConfirmed);
        Assert.Equal("Dropped", shamiso.Reason);
        Assert.Equal(_west1.Id, shamiso.RouteId);
        Assert.Equal(Friday, shamiso.TradingDate);

        var cheddar = extras.Breakages.Single(line => line.ItemCode == Cheddar);
        Assert.Equal(1m, cheddar.Units);
        Assert.False(cheddar.IsConfirmed);
        Assert.Null(cheddar.Reason);
    }

    /// <summary>
    /// A breakage is valued at what the vans sold the item for before VAT in the period, and not valued
    /// at all where they sold none: a guessed price would read as a real loss.
    /// </summary>
    [Fact]
    public async Task Breakages_are_valued_at_the_periods_van_price_before_vat()
    {
        AddSale(Rep, Monday, (Shamiso, 2m, 6.00m));
        AddSale(Rep, Tuesday, (Shamiso, 2m, 7.00m));
        AddBreakage(Rep, Friday, MarketBreakageStatuses.Pending, (Shamiso, 3m, null, "Leaking"), (Cheddar, 1m, null, null));
        await _context.SaveChangesAsync();

        var extras = await ExtrasAsync();

        Assert.Equal(19.50m, extras.Breakages.Single(line => line.ItemCode == Shamiso).ValueBeforeVat);
        Assert.Null(extras.Breakages.Single(line => line.ItemCode == Cheddar).ValueBeforeVat);
    }

    [Fact]
    public async Task A_period_wider_than_a_quarter_is_refused()
    {
        var result = await Handler().Handle(
            new GetVanManagementExtrasQuery(Monday, Monday.AddDays(120), []), CancellationToken.None);

        Assert.True(result.IsError);
    }

    private GetVanManagementExtrasHandler Handler() => new(_context, Options.Create(new TaxSettings()));

    private async Task<VanManagementExtrasResult> ExtrasAsync(params string[] items)
    {
        var result = await Handler().Handle(
            new GetVanManagementExtrasQuery(Monday, Friday, items), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private static User User(Guid id, string username, string account, int? routeId) => new()
    {
        Id = id,
        Username = username,
        Email = $"{username}@example.com",
        PasswordHash = "x",
        Role = "Sales",
        IsActive = true,
        AssignedBusinessPartnerCode = account,
        RouteId = routeId
    };

    /// <summary>An online sale as it is stored: before tax, on a reservation.</summary>
    private void AddSale(Guid rep, DateTime day, params (string Item, decimal Quantity, decimal NetPrice)[] lines)
    {
        var at = DateTime.SpecifyKind(day.AddHours(8), DateTimeKind.Utc);

        _context.StockReservations.Add(new StockReservationEntity
        {
            ReservationId = Guid.NewGuid().ToString(),
            ExternalReferenceId = Guid.NewGuid().ToString(),
            SourceSystem = "KefalosVanSales",
            DocumentType = ReservationDocumentType.Invoice,
            CardCode = VanAccount,
            CardName = "Van 009",
            RouteCustomerCode = "SHOP1",
            RouteCustomerName = "Shop 1",
            TotalValue = lines.Sum(line => line.Quantity * line.NetPrice),
            Currency = "USD",
            PaymentMethod = "Cash",
            Status = ReservationStatus.Confirmed,
            CreatedAt = at,
            ExpiresAt = at.AddHours(1),
            ConfirmedAt = at,
            CreatedBy = rep.ToString(),
            Lines = lines.Select((line, index) => new StockReservationLineEntity
            {
                LineNum = index,
                ItemCode = line.Item,
                ItemDescription = line.Item,
                OriginalQuantity = line.Quantity,
                ReservedQuantity = line.Quantity,
                UoMCode = "EA",
                WarehouseCode = VanAccount,
                UnitPrice = line.NetPrice,
                LineTotal = line.Quantity * line.NetPrice
            }).ToList()
        });
    }

    private void AddBreakage(
        Guid rep,
        DateTime day,
        string status,
        params (string Item, decimal Reported, decimal? Confirmed, string? Reason)[] lines)
    {
        _context.MarketBreakages.Add(new MarketBreakageEntity
        {
            ClientRequestId = Guid.NewGuid().ToString(),
            ReportedByUserId = rep,
            ReportedByName = "West One",
            VanWarehouseCode = VanAccount,
            CardName = "Shop 1",
            CapturedAtUtc = DateTime.SpecifyKind(day.AddHours(9), DateTimeKind.Utc),
            Status = status,
            Lines = lines.Select((line, index) => new MarketBreakageLineEntity
            {
                LineNum = index,
                ItemCode = line.Item,
                ItemDescription = line.Item,
                Reason = line.Reason,
                ReportedQuantity = line.Reported,
                ConfirmedQuantity = line.Confirmed
            }).ToList()
        });
    }
}
