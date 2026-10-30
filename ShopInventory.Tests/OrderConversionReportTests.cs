using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries.GetAdrPerformanceReport;
using ShopInventory.Features.VanSalesReports.Queries.GetOrderConversionReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the order conversion report: the sales reps' and ADRs' van orders followed to the sales they
/// became, split by who raised each one, the share of van sales that came from orders, the products,
/// the projection and the areas to look at.
/// </summary>
/// <remarks>
/// Seeded the way <see cref="AdrConversionReportTests"/> is: what a real van conversion leaves behind — a
/// reservation under the van's reference, its signed receipt, and the invoice queue entry naming the order.
/// </remarks>
public sealed class OrderConversionReportTests : IDisposable
{
    private static readonly Guid Adr = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid SalesRep = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid OtherSalesRep = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid Office = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime Day = new(2026, 9, 30);
    private static readonly DateTime Captured = new(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private int _orderNumber;

    public OrderConversionReportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        AddUser(Adr, "adr01", ApplicationRoles.Adr, "VAN015");
        AddUser(SalesRep, "van015", ApplicationRoles.Sales, "VAN015");
        AddUser(OtherSalesRep, "van016", " sales ", "VAN016");
        AddUser(Office, "office", ApplicationRoles.Manager, null);
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// The headline the report exists for: of what each kind of raiser ordered, how much became an actual
    /// sale — and the two sides add up to the whole.
    /// </summary>
    [Fact]
    public async Task Orders_are_split_by_who_raised_them_and_add_up_to_the_whole()
    {
        var repOrder = AddOrder(SalesRep, 115m, sapDocNum: 8001, routeCustomer: "SHOP1", lines: [("CHE011", 10m, 100m)]);
        var adrOrder = AddOrder(Adr, 230m, sapDocNum: 8002, routeCustomer: "SHOP2", lines: [("YOG002", 20m, 200m)]);
        await _context.SaveChangesAsync();
        AddConversion(repOrder, "VO-R1", 115m, [("CHE011", 10m, 100m)]);
        AddConversion(adrOrder, "VO-A1", 172.5m, [("YOG002", 15m, 150m)]);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        var overall = Assert.Single(report.Overall.Money);
        Assert.Equal(345m, overall.Ordered);
        Assert.Equal(287.5m, overall.Invoiced);
        Assert.Equal("USD", report.LeadCurrency);

        var reps = Assert.Single(report.Raisers, raiser => raiser.Role == OrderRaisers.SalesReps);
        var adrs = Assert.Single(report.Raisers, raiser => raiser.Role == OrderRaisers.Adrs);
        Assert.Equal(115m, Assert.Single(reps.Summary.Money).Invoiced);
        Assert.Equal(1.0, Assert.Single(reps.Summary.Money).Conversion!.Value, 3);
        Assert.Equal(172.5m, Assert.Single(adrs.Summary.Money).Invoiced);
        Assert.Equal(0.75, Assert.Single(adrs.Summary.Money).Conversion!.Value, 3);
        Assert.Equal(1, reps.ActivePeople);
        Assert.Equal(2, reps.People); // " sales " is a sales rep too
        Assert.Equal(1, adrs.ActivePeople);

        Assert.Equal(overall.Ordered, reps.Summary.Money.Sum(money => money.Ordered) + adrs.Summary.Money.Sum(money => money.Ordered));

        Assert.Equal(2, report.Reps.Count);
        Assert.Equal(OrderRaisers.Adrs, Assert.Single(report.Reps, rep => rep.UserId == Adr).Role);
        Assert.Contains(report.Orders, row => row.Order.UserId == SalesRep && row.Role == OrderRaisers.SalesReps);
        Assert.Contains(report.Orders, row => row.Order.UserId == Adr && row.Role == OrderRaisers.Adrs);

        var day = Assert.Single(report.Days);
        Assert.Equal(115m, day.SalesRepOrdered);
        Assert.Equal(230m, day.AdrOrdered);
        Assert.Equal(172.5m, day.AdrInvoiced);
    }

    /// <summary>
    /// Of everything the vans sold in the period, the part a converted order became is split by the
    /// order's raiser; the rest was sold on the spot. An order raised by someone else counts as other.
    /// </summary>
    [Fact]
    public async Task Van_sales_are_split_into_sales_from_orders_and_sales_on_the_spot()
    {
        var repOrder = AddOrder(SalesRep, 100m, sapDocNum: 8003, lines: [("CHE011", 10m, 86.96m)]);
        var adrOrder = AddOrder(Adr, 50m, sapDocNum: 8004, lines: [("CHE011", 5m, 43.48m)]);
        var officeOrder = AddOrder(Office, 30m, sapDocNum: 8005, lines: [("CHE011", 3m, 26.09m)], source: SalesOrderSource.Web);
        await _context.SaveChangesAsync();
        AddConversion(repOrder, "VO-R2", 100m, [("CHE011", 10m, 86.96m)]);
        AddConversion(adrOrder, "VO-A2", 50m, [("CHE011", 5m, 43.48m)]);
        AddConversion(officeOrder, "VO-O2", 30m, [("CHE011", 3m, 26.09m)]);
        AddOfflineSale(SalesRep, "SPOT-1", 220m, "VAN015");
        await _context.SaveChangesAsync();

        var report = await RunAsync();
        var mix = Assert.Single(report.SalesMix);

        Assert.Equal(400m, mix.VanSales);
        Assert.Equal(100m, mix.FromSalesRepOrders);
        Assert.Equal(50m, mix.FromAdrOrders);
        Assert.Equal(30m, mix.FromOtherOrders);
        Assert.Equal(220m, mix.Direct);
        Assert.Equal(3, mix.FromOrdersCount);
        Assert.Equal(0.45, mix.FromOrdersShare!.Value, 3);

        // The office's order is not a van rep's, so it is not followed: only the two van orders are.
        Assert.Equal(2, report.Overall.Counts.Ordered);
    }

    /// <summary>
    /// Products: what was ordered against what the van delivered, by raiser, with the units still on an
    /// order nobody has invoiced kept apart from a shortfall.
    /// </summary>
    [Fact]
    public async Task Products_show_ordered_delivered_short_and_not_invoiced_units()
    {
        var sold = AddOrder(SalesRep, 115m, sapDocNum: 8006, routeCustomer: "SHOP1",
            lines: [("CHE011", 10m, 50m), ("YOG002", 10m, 50m)]);
        AddOrder(Adr, 46m, routeCustomer: "SHOP2", lines: [("YOG002", 4m, 40m)]);
        await _context.SaveChangesAsync();
        AddConversion(sold, "VO-R3", 86.25m, [("CHE011", 10m, 50m), ("YOG002", 5m, 25m)]);
        await _context.SaveChangesAsync();

        var products = (await RunAsync()).Products;

        Assert.Equal(2, products.ProductsOrdered);
        Assert.Equal(2, products.ProductsSupplied);
        Assert.Equal(1, products.ProductsShort);
        Assert.Equal(24m, products.UnitsOrdered);
        Assert.Equal(15m, products.UnitsSupplied);
        Assert.Equal(4m, products.UnitsNotInvoiced);
        Assert.Equal(15.0 / 24, products.UnitConversion!.Value, 3);

        var yoghurt = Assert.Single(products.Items, item => item.ItemCode == "YOG002");
        Assert.Equal(14m, yoghurt.OrderedQuantity);
        Assert.Equal(5m, yoghurt.SuppliedQuantity);
        Assert.Equal(5m, yoghurt.ShortQuantity);
        Assert.Equal(4m, yoghurt.NotInvoicedQuantity);
        Assert.Equal(10m, yoghurt.SalesRepQuantity);
        Assert.Equal(4m, yoghurt.AdrQuantity);
        var money = Assert.Single(yoghurt.Money);
        Assert.Equal(28.75m, money.ShortSupplied);
        Assert.Equal(28.75m, money.Converted);
        Assert.Equal(103.5m, money.Ordered); // 57.50 (half of 115) + 46.00
    }

    /// <summary>A role narrows what is followed; the comparison between the two raisers stays whole.</summary>
    [Fact]
    public async Task Choosing_a_role_narrows_the_orders_but_not_the_split()
    {
        AddOrder(SalesRep, 100m, sapDocNum: 8007, lines: [("CHE011", 10m, 86.96m)]);
        AddOrder(Adr, 60m, lines: [("CHE011", 6m, 52.17m)]);
        await _context.SaveChangesAsync();

        var report = await RunAsync(role: "adr");

        Assert.Equal(60m, Assert.Single(report.Overall.Money).Ordered);
        Assert.All(report.Orders, row => Assert.Equal(Adr, row.Order.UserId));
        Assert.Equal(Adr, Assert.Single(report.Reps).UserId);
        Assert.Equal(2, report.Raisers.Count);
        Assert.Equal(100m, Assert.Single(report.Raisers.Single(raiser => raiser.Role == OrderRaisers.SalesReps).Summary.Money).Ordered);
    }

    [Fact]
    public async Task An_unknown_role_is_refused()
    {
        var result = await new GetOrderConversionReportHandler(_context, Options.Create(new TaxSettings())).Handle(
            new GetOrderConversionReportQuery(Day, Day, "Driver"),
            CancellationToken.None);

        Assert.True(result.IsError);
    }

    /// <summary>
    /// Orders that reached SAP and were never invoiced, and units the van did not deliver, are the two
    /// things a manager has to chase: both are raised, with the money at stake.
    /// </summary>
    [Fact]
    public async Task Lost_orders_and_short_supply_are_raised_for_attention()
    {
        var sold = AddOrder(SalesRep, 115m, sapDocNum: 8008, lines: [("CHE011", 10m, 50m), ("YOG002", 10m, 50m)]);
        AddOrder(Adr, 200m, sapDocNum: 8009, lines: [("CHE011", 20m, 173.91m)]);
        await _context.SaveChangesAsync();
        AddConversion(sold, "VO-R4", 86.25m, [("CHE011", 10m, 50m), ("YOG002", 5m, 25m)]);
        await _context.SaveChangesAsync();

        var attention = (await RunAsync()).Attention;

        var lost = Assert.Single(attention, item => item.Area == "Orders never invoiced");
        Assert.Equal("high", lost.Severity);
        Assert.Equal(200m, lost.ValueAtStake);
        Assert.Equal($"orders:{AdrOutcomes.NoLinkedInvoice}", lost.Focus);

        var shortage = Assert.Single(attention, item => item.Area == "Short supply");
        Assert.Equal(28.75m, shortage.ValueAtStake);
        Assert.Contains("YOG002", shortage.Detail);
        Assert.Equal("items", shortage.Focus);

        Assert.Equal("high", attention[0].Severity);
    }

    // ── The projection, without a database ─────────────────────────────────────

    /// <summary>
    /// Orders too young to have been invoiced are expected to end as the settled ones did, and the rest
    /// of the month is projected over the weekdays the reps order on.
    /// </summary>
    [Fact]
    public void The_projection_expects_open_orders_to_settle_like_the_rest_and_runs_to_month_end()
    {
        var today = new DateTime(2026, 10, 14); // a Wednesday
        var outcomes = new List<AdrConversion.Outcome>
        {
            // Settled: 100 ordered on Monday 5th, 80 invoiced; 100 on Tuesday 6th, 80 invoiced.
            Outcome(new DateTime(2026, 10, 5), 100m, 80m),
            Outcome(new DateTime(2026, 10, 6), 100m, 80m),
            // Open: 100 ordered on Tuesday 13th, nothing invoiced yet.
            Outcome(new DateTime(2026, 10, 13), 100m, null)
        };

        var projection = OrderConversionInsights.Project(outcomes, "USD", new DateTime(2026, 10, 1), today, today);

        Assert.Equal(0.8, projection.SettledConversion!.Value, 3);
        Assert.Equal(100m, projection.OpenOrdersValue);
        Assert.Equal(80m, projection.ExpectedFromOpenOrders);
        Assert.Equal(0.8, projection.ProjectedPeriodConversion!.Value, 3);

        // Mondays and Tuesdays only: 5, 6, 12, 13 Oct so far; 19, 20, 26, 27 Oct to come.
        Assert.Equal(4, projection.OrderDays);
        Assert.Equal(75m, projection.OrderedPerDay);
        Assert.Equal("the rest of October", projection.HorizonLabel);
        Assert.Equal(4, projection.HorizonDays);
        Assert.Equal(300m, projection.ProjectedOrdered);
        Assert.Equal(240m, projection.ProjectedConverted);
        Assert.Equal(600m, projection.ProjectedMonthOrdered);
        Assert.Equal(480m, projection.ProjectedMonthConverted);
        Assert.Equal(30m, projection.UpsideAtTarget); // 300 × (90% − 80%)
    }

    [Fact]
    public void A_period_ending_on_a_months_last_day_projects_the_next_month()
    {
        var outcomes = new List<AdrConversion.Outcome> { Outcome(new DateTime(2026, 9, 30), 100m, 100m) };

        var projection = OrderConversionInsights.Project(outcomes, "USD", new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), new DateTime(2026, 10, 30));

        Assert.Equal("October 2026", projection.HorizonLabel);
        Assert.Equal(new DateTime(2026, 10, 1), projection.HorizonFrom);
        Assert.Equal(new DateTime(2026, 10, 31), projection.HorizonTo);
        Assert.Null(projection.ProjectedMonthOrdered);
        Assert.Equal(0m, projection.UpsideAtTarget);
    }

    /// <summary>
    /// On the 30th the rest of the month is one day, which says nothing: the horizon moves to the next
    /// month, and the month-end figure still closes this one.
    /// </summary>
    [Fact]
    public void With_under_a_week_left_the_horizon_is_the_next_month_and_month_end_still_closes()
    {
        var today = new DateTime(2026, 10, 30); // a Friday
        var outcomes = new List<AdrConversion.Outcome>
        {
            Outcome(new DateTime(2026, 10, 2), 100m, 100m),   // Friday
            Outcome(new DateTime(2026, 10, 3), 100m, 100m)    // Saturday
        };

        var projection = OrderConversionInsights.Project(outcomes, "USD", new DateTime(2026, 10, 1), today, today);

        Assert.Equal("November 2026", projection.HorizonLabel);
        Assert.Equal(8, projection.HorizonDays);                // the Fridays and Saturdays of November
        Assert.Equal(9, projection.OrderDays);                  // the Fridays and Saturdays of 1-30 October
        Assert.Equal(22.22m, projection.OrderedPerDay);
        Assert.Equal(222.22m, projection.ProjectedMonthOrdered); // 200 + Saturday 31st
    }

    [Fact]
    public void The_weekly_trend_is_the_slope_in_points_a_week()
    {
        var weeks = new List<OrderConversionWeekResult>
        {
            new(new DateTime(2026, 9, 7), 1, 100m, 90m),
            new(new DateTime(2026, 9, 14), 1, 100m, 85m),
            new(new DateTime(2026, 9, 21), 1, 100m, 80m)
        };

        Assert.Equal(-5.0, OrderConversionInsights.Trend(weeks)!.Value, 3);
        Assert.Null(OrderConversionInsights.Trend(weeks.Take(2).ToList()));
    }

    // ── Seeding ─────────────────────────────────────────────────────────────────

    private static AdrConversion.Outcome Outcome(DateTime day, decimal ordered, decimal? invoiced) =>
        new(
            new GetAdrPerformanceReportHandler.VanOrderFact(SalesRep, day, SalesOrderStatus.Approved, true, "USD", ordered, null)
            {
                OrderedAtUtc = day.AddHours(6)
            },
            invoiced is null ? AdrOutcomes.NoLinkedInvoice : invoiced < ordered ? AdrOutcomes.Repriced : AdrOutcomes.InvoicedInFull,
            ordered,
            invoiced,
            0m,
            invoiced is { } sold ? ordered - sold : 0m,
            invoiced is null ? null : day.AddHours(10),
            invoiced is null ? null : day,
            invoiced is null ? null : 0.2,
            null,
            null,
            false,
            [],
            []);

    private async Task<OrderConversionReportResult> RunAsync(string? role = null, Guid? userId = null)
    {
        var result = await new GetOrderConversionReportHandler(_context, Options.Create(new TaxSettings())).Handle(
            new GetOrderConversionReportQuery(Day, Day, role, userId),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
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

    private SalesOrderEntity AddOrder(
        Guid userId,
        decimal total,
        (string Item, decimal Quantity, decimal Net)[] lines,
        string? routeCustomer = null,
        int? sapDocNum = null,
        SalesOrderSource source = SalesOrderSource.Mobile)
    {
        var order = new SalesOrderEntity
        {
            OrderNumber = $"SO-20260930-{++_orderNumber:D4}",
            OrderDate = Captured,
            CardCode = "VAN015",
            Currency = "USD",
            Lines = lines
                .Select((line, index) => new SalesOrderLineEntity
                {
                    LineNum = index,
                    ItemCode = line.Item,
                    ItemDescription = $"Item {line.Item}",
                    Quantity = line.Quantity,
                    UnitPrice = line.Quantity == 0 ? 0 : line.Net / line.Quantity,
                    LineTotal = line.Net
                })
                .ToList(),
            Status = sapDocNum is null ? SalesOrderStatus.Pending : SalesOrderStatus.Approved,
            Source = source,
            SAPDocNum = sapDocNum,
            DocTotal = total,
            RouteCustomerCode = routeCustomer,
            RouteCustomerName = routeCustomer is null ? null : $"Shop {routeCustomer}",
            CreatedByUserId = userId
        };

        _context.SalesOrders.Add(order);
        return order;
    }

    /// <summary>What a van conversion leaves: the reservation, its signed receipt and the queue entry.</summary>
    private void AddConversion(
        SalesOrderEntity order,
        string reference,
        decimal receiptTotal,
        (string Item, decimal Quantity, decimal Net)[] lines)
    {
        var at = Captured.AddHours(2);
        var reservation = new StockReservationEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = "VAN015",
            Currency = "USD",
            TotalValue = lines.Sum(line => line.Net),
            Status = ReservationStatus.Confirmed,
            CreatedAt = at,
            ExpiresAt = at.AddHours(1),
            CreatedBy = SalesRep.ToString(),
            RouteCustomerCode = order.RouteCustomerCode,
            Lines = lines
                .Select((line, index) => new StockReservationLineEntity
                {
                    LineNum = index,
                    ItemCode = line.Item,
                    OriginalQuantity = line.Quantity,
                    ReservedQuantity = line.Quantity,
                    WarehouseCode = "VAN015",
                    UnitPrice = line.Quantity == 0 ? 0 : line.Net / line.Quantity,
                    LineTotal = line.Net
                })
                .ToList()
        };

        _context.StockReservations.Add(reservation);

        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.VanSalesOnline,
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            CardCode = "VAN015",
            CardName = "Van 015",
            DocDate = at.Date,
            TotalAmount = receiptTotal,
            VatAmount = 0m,
            Currency = "USD",
            WarehouseCode = "VAN015",
            PaymentMethod = "Cash",
            AmountPaid = receiptTotal,
            CreatedBy = SalesRep.ToString()
        });

        _context.InvoiceQueue.Add(new InvoiceQueueEntity
        {
            ReservationId = reservation.ReservationId,
            ExternalReference = reference,
            CustomerCode = "VAN015",
            InvoicePayload = "{}",
            Status = InvoiceQueueStatus.Completed,
            SalesOrderId = order.Id,
            TotalAmount = receiptTotal,
            Currency = "USD",
            SourceSystem = SaleSourceSystems.VanSales,
            CreatedAt = at
        });
    }

    private void AddOfflineSale(Guid userId, string reference, decimal total, string account) =>
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = account,
            CardName = account,
            DocDate = Day,
            TotalAmount = total,
            VatAmount = 0m,
            Currency = "USD",
            WarehouseCode = account,
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
