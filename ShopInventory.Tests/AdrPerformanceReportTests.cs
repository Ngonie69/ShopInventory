using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries.GetAdrPerformanceReport;
using ShopInventory.Features.VanSalesReports.Queries.GetVanSalesPerformanceReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the ADR performance report: who counts as an ADR, which orders are van orders, and the
/// shares, which are only worth reading if every one of them is measured against the same whole.
/// </summary>
public sealed class AdrPerformanceReportTests : IDisposable
{
    private static readonly Guid Adr = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid OtherAdr = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid SalesRep = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid Merchandiser = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly DateTime Day = new(2026, 9, 30);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private int _orderNumber;

    public AdrPerformanceReportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        AddUser(Adr, "adr01", ApplicationRoles.Adr, "VAN015");
        AddUser(OtherAdr, "adr02", ApplicationRoles.Adr, "VAN016");
        AddUser(SalesRep, "van010", ApplicationRoles.Sales, "VAN010");
        AddUser(Merchandiser, "merch01", ApplicationRoles.Merchandiser, null);
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// The ADRs' share is measured against every van rep's trade, so a Sales rep's orders and sales
    /// are in the whole and out of the part.
    /// </summary>
    [Fact]
    public async Task The_adrs_share_is_measured_against_every_van_rep()
    {
        AddOrder(Adr, 300m, routeCustomer: "SHOP1");
        AddOrder(SalesRep, 100m, routeCustomer: "SHOP2");
        AddSale(Adr, "S-1", 60m, "SHOP1");
        AddSale(SalesRep, "S-2", 140m, "SHOP2");
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        var orders = Assert.Single(report.Overall.Orders);
        Assert.Equal(300m, orders.AdrGross);
        Assert.Equal(400m, orders.VanGross);
        Assert.Equal(0.75, orders.Share!.Value, 3);

        var sales = Assert.Single(report.Overall.Sales);
        Assert.Equal(60m, sales.AdrGross);
        Assert.Equal(200m, sales.VanGross);
        Assert.Equal(0.30, sales.Share!.Value, 3);

        Assert.DoesNotContain(report.Adrs, row => row.UserId == SalesRep);
    }

    /// <summary>
    /// A merchandiser's mobile order is not a van order, and a web order is not a mobile one; neither
    /// may inflate the whole the ADRs are measured against.
    /// </summary>
    [Fact]
    public async Task Orders_from_outside_the_vans_are_not_in_the_whole()
    {
        AddOrder(Adr, 100m);
        AddOrder(Merchandiser, 900m);
        AddOrder(Adr, 500m, source: SalesOrderSource.Web);
        await _context.SaveChangesAsync();

        var orders = Assert.Single((await RunAsync()).Overall.Orders);

        Assert.Equal(100m, orders.VanGross);
        Assert.Equal(1.0, orders.Share!.Value, 3);
    }

    [Fact]
    public async Task Each_adr_gets_their_own_counts_customers_and_shares()
    {
        AddOrder(Adr, 100m, routeCustomer: "SHOP1", sapDocNum: 83167);
        AddOrder(Adr, 50m, routeCustomer: "shop1", status: SalesOrderStatus.Fulfilled, sapDocNum: 83172);
        AddOrder(Adr, 80m, routeCustomer: "SHOP3", status: SalesOrderStatus.Cancelled);
        AddOrder(Adr, 25m, routeCustomer: "SHOP4");
        AddOrder(OtherAdr, 75m, routeCustomer: "SHOP9", sapDocNum: 83180);
        AddSale(Adr, "S-1", 40m, "SHOP1");
        await _context.SaveChangesAsync();

        var report = await RunAsync();
        var row = Assert.Single(report.Adrs, adr => adr.UserId == Adr);

        Assert.Equal(4, row.OrderCounts.Total);
        Assert.Equal(2, row.OrderCounts.InSap);
        Assert.Equal(1, row.OrderCounts.Fulfilled);
        Assert.Equal(1, row.OrderCounts.Pending);
        Assert.Equal(1, row.OrderCounts.Cancelled);

        // Shop codes compare without case, and a cancelled order's shop was still ordered for.
        Assert.Equal(3, row.OrderCustomerCount);
        Assert.Equal(1, row.SaleCustomerCount);

        // A cancelled order is counted but carries no money.
        var orderMoney = Assert.Single(row.OrderTotalsByCurrency);
        Assert.Equal(175m, orderMoney.Gross);
        Assert.Equal(3, orderMoney.DocumentCount);

        var share = Assert.Single(row.Shares);
        Assert.Equal(175.0 / 250.0, share.OrderShare!.Value, 3);
        Assert.Equal(1.0, share.SalesShare!.Value, 3);

        // The first row is the one that sold most.
        Assert.Equal(Adr, report.Adrs[0].UserId);
        Assert.Equal(2, report.Overall.ActiveAdrCount);
    }

    /// <summary>
    /// The sales side must be the same figure the performance report shows for the rep — both read
    /// the one fact stream.
    /// </summary>
    [Fact]
    public async Task An_adrs_sales_agree_with_the_performance_report()
    {
        AddSale(Adr, "S-1", 40m, "SHOP1");
        AddSale(Adr, "S-2", 25m, "SHOP2");
        AddSale(Adr, "S-3", 900m, "SHOP2", currency: "ZWG");
        await _context.SaveChangesAsync();

        var row = Assert.Single((await RunAsync()).Adrs, adr => adr.UserId == Adr);

        var performance = await new GetVanSalesPerformanceReportHandler(_context).Handle(
            new GetVanSalesPerformanceReportQuery(Day, Day, Adr),
            CancellationToken.None);
        var rep = Assert.Single(performance.Value.Reps);

        Assert.Equal(
            rep.TotalsByCurrency.Select(total => (total.Currency, total.Gross)),
            row.SalesTotalsByCurrency.Select(total => (total.Currency, total.Gross)));
    }

    /// <summary>
    /// An order placed at 23:30 CAT is 21:30 UTC — the same trading day, not the next one, and not
    /// outside a window that ends that day.
    /// </summary>
    [Fact]
    public async Task Orders_are_dated_by_the_cat_trading_day()
    {
        AddOrder(Adr, 10m, orderDateUtc: new DateTime(2026, 9, 30, 21, 30, 0, DateTimeKind.Utc));
        AddOrder(Adr, 20m, orderDateUtc: new DateTime(2026, 9, 30, 22, 30, 0, DateTimeKind.Utc));
        await _context.SaveChangesAsync();

        var row = Assert.Single((await RunAsync()).Adrs, adr => adr.UserId == Adr);

        Assert.Equal(10m, Assert.Single(row.OrderTotalsByCurrency).Gross);
    }

    [Fact]
    public async Task An_active_adr_with_nothing_is_listed_and_an_inactive_one_is_not()
    {
        _context.Users.Single(user => user.Id == OtherAdr).IsActive = false;
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        var row = Assert.Single(report.Adrs);
        Assert.Equal(Adr, row.UserId);
        Assert.Equal(0, row.ActiveDays);
        Assert.Empty(row.Shares);
        Assert.Equal(1, report.Overall.AdrCount);
        Assert.Equal(0, report.Overall.ActiveAdrCount);
    }

    [Fact]
    public async Task One_adr_narrows_the_rows_but_not_the_whole()
    {
        AddOrder(Adr, 100m);
        AddOrder(OtherAdr, 300m);
        await _context.SaveChangesAsync();

        var report = await RunAsync(userId: OtherAdr);

        var row = Assert.Single(report.Adrs);
        Assert.Equal(OtherAdr, row.UserId);
        Assert.Equal(0.75, Assert.Single(row.Shares).OrderShare!.Value, 3);
    }

    [Fact]
    public async Task A_period_that_ends_before_it_starts_is_refused()
    {
        var result = await new GetAdrPerformanceReportHandler(_context).Handle(
            new GetAdrPerformanceReportQuery(Day, Day.AddDays(-1)),
            CancellationToken.None);

        Assert.True(result.IsError);
    }

    private async Task<AdrPerformanceReportResult> RunAsync(Guid? userId = null)
    {
        var result = await new GetAdrPerformanceReportHandler(_context).Handle(
            new GetAdrPerformanceReportQuery(Day, Day, userId),
            CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }

    private void AddUser(Guid id, string username, string role, string? account) =>
        _context.Users.Add(new User
        {
            Id = id,
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = "x",
            Role = role,
            IsActive = true,
            FirstName = username.ToUpperInvariant(),
            AssignedBusinessPartnerCode = account
        });

    private void AddOrder(
        Guid userId,
        decimal total,
        string? routeCustomer = null,
        SalesOrderStatus? status = null,
        int? sapDocNum = null,
        SalesOrderSource source = SalesOrderSource.Mobile,
        DateTime? orderDateUtc = null) =>
        _context.SalesOrders.Add(new SalesOrderEntity
        {
            OrderNumber = $"SO-20260930-{++_orderNumber:D4}",
            OrderDate = orderDateUtc ?? new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc),
            CardCode = "VAN015",
            Currency = "USD",
            // The context refuses an Approved order with no SAP number, as production does.
            Status = status ?? (sapDocNum is null ? SalesOrderStatus.Pending : SalesOrderStatus.Approved),
            Source = source,
            SAPDocNum = sapDocNum,
            DocTotal = total,
            RouteCustomerCode = routeCustomer,
            CreatedByUserId = userId
        });

    private void AddSale(Guid userId, string reference, decimal total, string? routeCustomer, string currency = "USD") =>
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = "KefalosVanSales",
            CardCode = "VAN015",
            CardName = "Van 015",
            RouteCustomerCode = routeCustomer,
            DocDate = Day,
            TotalAmount = total,
            VatAmount = 0m,
            Currency = currency,
            WarehouseCode = "VAN015",
            PaymentMethod = "Cash",
            AmountPaid = total,
            CreatedBy = userId.ToString()
        });

    /// <summary>See MobileOrderCreditHoldTests: SQLite has no store-generated row version.</summary>
    private sealed class SqliteApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<SalesOrderEntity>()
                .Property(order => order.RowVersion)
                .IsConcurrencyToken(false)
                .HasDefaultValue(new byte[] { 1 });
        }
    }
}
