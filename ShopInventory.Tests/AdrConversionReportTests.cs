using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries.GetAdrPerformanceReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the ADR report's order-to-sale figures: an ADR order followed through the invoice queue to the
/// van sale it became, and the gap between the two split into reasons that add back up to it.
/// </summary>
/// <remarks>
/// The seeds are what a real van conversion leaves behind (ConvertSalesOrderToInvoiceHandler): a
/// reservation under the van's reference, raised by the van rep; the signed receipt row that carries its
/// VAT-inclusive total; and the invoice queue entry naming the order.
/// </remarks>
public sealed class AdrConversionReportTests : IDisposable
{
    private static readonly Guid Adr = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid OtherAdr = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid VanRep = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly DateTime Day = new(2026, 9, 30);
    private static readonly DateTime Captured = new(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private int _orderNumber;

    public AdrConversionReportTests()
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
        AddUser(VanRep, "van015", ApplicationRoles.Sales, "VAN015");
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task An_order_the_van_invoiced_as_ordered_converts_in_full()
    {
        var order = AddOrder(Adr, 115m, sapDocNum: 7001, lines: [("CHE011", 10m, 100m)]);
        await _context.SaveChangesAsync();
        AddConversion(order, "VO-1", 115m, [("CHE011", 10m, 100m)], invoicedAtUtc: Captured.AddHours(3), sapDocNum: 418001);
        await _context.SaveChangesAsync();

        var conversion = (await RunAsync()).Conversion!;
        var money = Assert.Single(conversion.Overall.Money);

        Assert.Equal(115m, money.Ordered);
        Assert.Equal(115m, money.Invoiced);
        Assert.Equal(1.0, money.Conversion!.Value, 3);
        Assert.Equal(1.0, money.FillRate!.Value, 3);
        Assert.Equal(1, conversion.Overall.Counts.InvoicedInFull);
        Assert.Equal(0.125, conversion.Overall.MedianDaysToInvoice!.Value, 3);
        Assert.Equal(1.0, conversion.Overall.InvoicedByNextDay!.Value, 3);

        var line = Assert.Single(conversion.Orders);
        Assert.Equal(AdrOutcomes.InvoicedInFull, line.Outcome);
        Assert.Equal("VO-1", line.InvoiceReference);
        Assert.Equal(418001, line.InvoiceDocNum);
        Assert.Equal(0m, line.Variance);
    }

    /// <summary>
    /// The van sent fewer units of one item. The shortfall is valued at the order's own prices, scaled
    /// from its net lines to its VAT-inclusive total, and it is all of the gap.
    /// </summary>
    [Fact]
    public async Task Units_the_van_did_not_deliver_are_short_supply_at_the_orders_prices()
    {
        var order = AddOrder(Adr, 115m, sapDocNum: 7002, routeCustomer: "SHOP1",
            lines: [("CHE011", 10m, 50m), ("YOG002", 10m, 50m)]);
        await _context.SaveChangesAsync();
        AddConversion(order, "VO-2", 86.25m, [("CHE011", 10m, 50m), ("YOG002", 5m, 25m)]);
        await _context.SaveChangesAsync();

        var conversion = (await RunAsync()).Conversion!;
        var money = Assert.Single(conversion.Overall.Money);

        Assert.Equal(86.25m, money.Invoiced);
        Assert.Equal(28.75m, money.ShortSupplied);
        Assert.Equal(0m, money.Repriced);
        Assert.Equal(0.75, money.FillRate!.Value, 3);
        Assert.Equal(AdrOutcomes.ShortSupplied, Assert.Single(conversion.Orders).Outcome);

        var yoghurt = Assert.Single(conversion.Items, item => item.ItemCode == "YOG002");
        Assert.Equal(10m, yoghurt.OrderedQuantity);
        Assert.Equal(5m, yoghurt.SuppliedQuantity);
        Assert.Equal(5m, yoghurt.ShortQuantity);
        Assert.Equal(0.5, yoghurt.FillRate!.Value, 3);
        Assert.Equal(1, yoghurt.ShortShopCount);
        Assert.Equal(28.75m, Assert.Single(yoghurt.Money).ShortSupplied);
        Assert.Equal("YOG002", conversion.Items[0].ItemCode);

        var shop = Assert.Single(conversion.Shops);
        Assert.Equal(1, shop.ShortSupplied);
    }

    /// <summary>
    /// Every order is in one outcome, and the five reasons add up to exactly what was ordered and not
    /// sold — otherwise the gap on the page could not be reconciled to anything.
    /// </summary>
    [Fact]
    public async Task The_reasons_add_up_to_the_gap_and_every_order_is_counted_once()
    {
        var sold = AddOrder(Adr, 115m, sapDocNum: 7003, lines: [("CHE011", 10m, 100m)]);
        AddOrder(Adr, 20m, status: SalesOrderStatus.Cancelled, lines: [("CHE011", 1m, 17.39m)]);
        AddOrder(Adr, 30m, lines: [("CHE011", 2m, 26.09m)]);
        AddOrder(Adr, 40m, sapDocNum: 7004, lines: [("CHE011", 3m, 34.78m)]);
        await _context.SaveChangesAsync();
        // Same units, a lower price: a reprice, not a shortfall.
        AddConversion(sold, "VO-3", 100m, [("CHE011", 10m, 86.96m)]);
        await _context.SaveChangesAsync();

        var overall = (await RunAsync()).Conversion!.Overall;
        var money = Assert.Single(overall.Money);

        Assert.Equal(205m, money.Ordered);
        Assert.Equal(100m, money.Invoiced);
        Assert.Equal(15m, money.Repriced);
        Assert.Equal(20m, money.Cancelled);
        Assert.Equal(30m, money.NotYetInSap);
        Assert.Equal(40m, money.NoLinkedInvoice);
        Assert.Equal(money.Gap, money.ShortSupplied + money.Repriced + money.Cancelled + money.NotYetInSap + money.NoLinkedInvoice);

        var counts = overall.Counts;
        Assert.Equal(4, counts.Ordered);
        Assert.Equal(1, counts.Repriced);
        Assert.Equal(1, counts.Cancelled);
        Assert.Equal(1, counts.NotYetInSap);
        Assert.Equal(1, counts.NoLinkedInvoice);
        Assert.Equal(2, counts.ReachedSap);
        Assert.Equal(counts.Ordered,
            counts.InvoicedInFull + counts.ShortSupplied + counts.Repriced + counts.NoLinkedInvoice + counts.NotYetInSap + counts.Cancelled);
    }

    /// <summary>An order taken on the last day is often invoiced on the first day of the next period.</summary>
    [Fact]
    public async Task An_invoice_made_after_the_period_still_counts_for_the_order()
    {
        var order = AddOrder(Adr, 50m, sapDocNum: 7005, lines: [("CHE011", 5m, 43.48m)]);
        await _context.SaveChangesAsync();
        AddConversion(order, "VO-4", 50m, [("CHE011", 5m, 43.48m)], invoicedAtUtc: Captured.AddDays(1).AddHours(2));
        await _context.SaveChangesAsync();

        var conversion = (await RunAsync()).Conversion!;

        Assert.Equal(50m, Assert.Single(conversion.Overall.Money).Invoiced);
        Assert.Equal(1.083, conversion.Overall.MedianDaysToInvoice!.Value, 2);
        Assert.Equal(1.0, conversion.Overall.InvoicedByNextDay!.Value, 3);
    }

    /// <summary>
    /// A cancelled queue entry is an invoice that never happened, so the order it named is not sold.
    /// </summary>
    [Fact]
    public async Task A_cancelled_invoice_does_not_make_the_order_a_sale()
    {
        var order = AddOrder(Adr, 50m, sapDocNum: 7006, lines: [("CHE011", 5m, 43.48m)]);
        await _context.SaveChangesAsync();
        AddConversion(order, "VO-5", 50m, [("CHE011", 5m, 43.48m)], queueStatus: InvoiceQueueStatus.Cancelled,
            reservationStatus: ReservationStatus.Cancelled);
        await _context.SaveChangesAsync();

        var conversion = (await RunAsync()).Conversion!;

        Assert.Equal(0m, Assert.Single(conversion.Overall.Money).Invoiced);
        Assert.Equal(AdrOutcomes.NoLinkedInvoice, Assert.Single(conversion.Orders).Outcome);
    }

    /// <summary>
    /// The share is of everything the serving van took in the period, so its own spot sales are in the
    /// whole and out of the part.
    /// </summary>
    [Fact]
    public async Task The_share_of_van_sales_is_out_of_everything_the_serving_van_took()
    {
        var order = AddOrder(Adr, 100m, sapDocNum: 7007, lines: [("CHE011", 10m, 86.96m)]);
        await _context.SaveChangesAsync();
        AddConversion(order, "VO-6", 100m, [("CHE011", 10m, 86.96m)]);
        AddOfflineSale(VanRep, "SPOT-1", 200m, "VAN015");
        AddOfflineSale(VanRep, "ELSEWHERE", 900m, "VAN099");
        await _context.SaveChangesAsync();

        var money = Assert.Single((await RunAsync()).Conversion!.Overall.Money);

        Assert.Equal(300m, money.VanSales);
        Assert.Equal(100m, money.AdrLedVanSales);
        Assert.Equal(1.0 / 3, money.VanShare!.Value, 3);
    }

    [Fact]
    public async Task An_order_captured_without_prices_takes_its_value_from_the_invoice()
    {
        var order = AddOrder(Adr, 0m, sapDocNum: 7008, lines: [("CHE011", 4m, 0m)]);
        await _context.SaveChangesAsync();
        AddConversion(order, "VO-7", 46m, [("CHE011", 2m, 40m)]);
        await _context.SaveChangesAsync();

        var report = await RunAsync();
        var money = Assert.Single(report.Conversion!.Overall.Money);

        Assert.Equal(46m, money.Ordered);
        Assert.Equal(46m, money.Invoiced);
        Assert.Equal(0m, money.ShortSupplied);
        Assert.Contains(report.Caveats, caveat => caveat.Contains("captured without prices"));
    }

    [Fact]
    public async Task Each_adr_gets_a_row_and_one_adr_narrows_the_lists_but_not_the_whole()
    {
        var mine = AddOrder(Adr, 115m, sapDocNum: 7009, routeCustomer: "SHOP1", lines: [("CHE011", 10m, 100m)]);
        AddOrder(OtherAdr, 60m, routeCustomer: "SHOP9", lines: [("YOG002", 6m, 52.17m)]);
        await _context.SaveChangesAsync();
        AddConversion(mine, "VO-8", 115m, [("CHE011", 10m, 100m)]);
        await _context.SaveChangesAsync();

        var conversion = (await RunAsync(userId: Adr)).Conversion!;

        Assert.Equal(175m, Assert.Single(conversion.Overall.Money).Ordered);
        var row = Assert.Single(conversion.Adrs);
        Assert.Equal(Adr, row.UserId);
        Assert.Equal(115m, Assert.Single(row.Summary.Money).Invoiced);
        Assert.All(conversion.Orders, order => Assert.Equal(Adr, order.UserId));
        Assert.Equal("SHOP1", Assert.Single(conversion.Shops).CustomerCode);
        var day = Assert.Single(conversion.Days);
        Assert.Equal(Day, day.TradingDate);
        Assert.Equal(115m, Assert.Single(day.Money).Invoiced);
    }

    [Fact]
    public async Task The_previous_period_is_measured_the_same_way()
    {
        var earlier = AddOrder(Adr, 80m, sapDocNum: 7010, orderDateUtc: Captured.AddDays(-1), lines: [("CHE011", 8m, 69.57m)]);
        AddOrder(Adr, 40m, sapDocNum: 7011, lines: [("CHE011", 4m, 34.78m)]);
        await _context.SaveChangesAsync();
        AddConversion(earlier, "VO-9", 60m, [("CHE011", 6m, 52.17m)], invoicedAtUtc: Captured.AddDays(-1).AddHours(4));
        await _context.SaveChangesAsync();

        var conversion = (await RunAsync()).Conversion!;
        var previous = Assert.IsType<AdrConversionComparisonResult>(conversion.Previous);

        Assert.Equal(Day.AddDays(-1), previous.FromDate);
        Assert.Equal(Day.AddDays(-1), previous.ToDate);
        Assert.Equal(0.75, Assert.Single(previous.Overall.Money).Conversion!.Value, 3);
        Assert.Equal(0.0, Assert.Single(conversion.Overall.Money).Conversion!.Value, 3);
    }

    [Theory]
    [InlineData("2026-10-01", "2026-10-28", "2026-09-01", "2026-09-28")]
    [InlineData("2026-03-01", "2026-03-31", "2026-02-01", "2026-02-28")]
    [InlineData("2026-10-10", "2026-10-16", "2026-10-03", "2026-10-09")]
    [InlineData("2026-09-25", "2026-10-05", "2026-09-14", "2026-09-24")]
    public void The_comparison_is_the_same_days_last_month_or_the_days_just_before(
        string from, string to, string expectedFrom, string expectedTo)
    {
        var (previousFrom, previousTo) = AdrConversion.PreviousPeriod(DateTime.Parse(from), DateTime.Parse(to));

        Assert.Equal(DateTime.Parse(expectedFrom), previousFrom);
        Assert.Equal(DateTime.Parse(expectedTo), previousTo);
    }

    // ── Seeding ─────────────────────────────────────────────────────────────────

    private async Task<AdrPerformanceReportResult> RunAsync(Guid? userId = null)
    {
        var result = await new GetAdrPerformanceReportHandler(_context, Options.Create(new TaxSettings())).Handle(
            new GetAdrPerformanceReportQuery(Day, Day, userId),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.NotNull(result.Value.Conversion);
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
        SalesOrderStatus? status = null,
        int? sapDocNum = null,
        DateTime? orderDateUtc = null)
    {
        var order = new SalesOrderEntity
        {
            OrderNumber = $"SO-20260930-{++_orderNumber:D4}",
            OrderDate = orderDateUtc ?? Captured,
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
            Status = status ?? (sapDocNum is null ? SalesOrderStatus.Pending : SalesOrderStatus.Approved),
            Source = SalesOrderSource.Mobile,
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
        (string Item, decimal Quantity, decimal Net)[] lines,
        DateTime? invoicedAtUtc = null,
        int? sapDocNum = null,
        InvoiceQueueStatus queueStatus = InvoiceQueueStatus.Completed,
        string reservationStatus = ReservationStatus.Confirmed)
    {
        var at = invoicedAtUtc ?? Captured.AddHours(2);
        var reservation = new StockReservationEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = "VAN015",
            Currency = "USD",
            TotalValue = lines.Sum(line => line.Net),
            Status = reservationStatus,
            CreatedAt = at,
            ExpiresAt = at.AddHours(1),
            CreatedBy = VanRep.ToString(),
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
            CreatedBy = VanRep.ToString()
        });

        _context.InvoiceQueue.Add(new InvoiceQueueEntity
        {
            ReservationId = reservation.ReservationId,
            ExternalReference = reference,
            CustomerCode = "VAN015",
            InvoicePayload = "{}",
            Status = queueStatus,
            SapDocNum = sapDocNum,
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
