using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Features.VanSalesReports.Queries.GetVanSalesAnalysis;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the van sales breakdown: the desktop analysis's shape, read from both tables a van sale lands in.
/// </summary>
/// <remarks>
/// The first case is the reason the query exists. The desktop analysis confined to vans reads
/// <c>DesktopSales</c> alone, and an online van sale never becomes one — so every sale a van made with
/// signal would be missing, with nothing on the page to say so.
/// </remarks>
public sealed class VanSalesAnalysisTests : IDisposable
{
    private const string Van = "VAN010";
    private const string OtherVan = "VAN020";
    private const string VanAccount = "VAN010";

    private static readonly Guid Rep = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime Day = new(2026, 8, 10);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesAnalysisTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Online_and_offline_sales_are_both_counted()
    {
        AddOfflineSale("OFF-1", total: 40m, vat: 5m);
        AddOnlineSale("ON-1", Utc(9, 0), total: 60m);
        await _context.SaveChangesAsync();

        var usd = Usd(await AnalyseAsync());

        Assert.Equal(2, usd.SalesCount);
        Assert.Equal(100m, usd.TotalAmount);
        // VAT is the offline receipt's; the online sale's is on its SAP invoice.
        Assert.Equal(5m, usd.VatAmount);
        Assert.Equal(95m, usd.NetAmount);

        Assert.Equal(
            new[] { ("offline", 40m), ("online", 60m) },
            usd.BySource.Select(row => (row.Key, row.TotalAmount)).OrderBy(row => row.Key));
    }

    /// <summary>
    /// An online van sale's receipt row carries a sale already counted as its reservation. Adding it in
    /// would count the money twice.
    /// </summary>
    [Fact]
    public async Task Online_receipt_rows_are_not_counted_a_second_time()
    {
        AddOnlineSale("ON-1", Utc(9, 0), total: 60m);
        AddOfflineSale("ON-1", total: 60m, sourceSystem: "KefalosVanSalesOnline");
        await _context.SaveChangesAsync();

        var usd = Usd(await AnalyseAsync());

        Assert.Equal(1, usd.SalesCount);
        Assert.Equal(60m, usd.TotalAmount);
    }

    /// <summary>
    /// A reservation carries its warehouse on its lines, and a van filter that could not see it would drop
    /// every online sale.
    /// </summary>
    [Fact]
    public async Task A_van_filter_reaches_online_sales_through_their_lines()
    {
        AddOfflineSale("OFF-1", total: 40m);
        AddOnlineSale("ON-1", Utc(9, 0), total: 60m);
        AddOnlineSale("ON-2", Utc(9, 30), total: 25m, warehouse: OtherVan);
        await _context.SaveChangesAsync();

        var all = Usd(await AnalyseAsync());
        Assert.Equal(
            new[] { (Van, 100m), (OtherVan, 25m) },
            all.ByWarehouse.Select(row => (row.Key, row.TotalAmount)));

        var one = await AnalyseAsync(warehouse: Van);
        Assert.Equal(Van, one.WarehouseCode);
        Assert.Equal(100m, Usd(one).TotalAmount);
        Assert.Equal(2, Usd(one).TopItems.Single().SalesCount);
    }

    /// <summary>
    /// The document's card is the van's own account on every sale it makes; the buyer is the route
    /// customer.
    /// </summary>
    [Fact]
    public async Task Customers_are_the_route_customers_not_the_van_account()
    {
        AddOfflineSale("OFF-1", total: 40m, routeCustomer: "TUCK01");
        AddOfflineSale("OFF-2", total: 10m, routeCustomer: "TUCK01");
        AddOnlineSale("ON-1", Utc(9, 0), total: 60m, routeCustomer: "CORNER1");
        AddOnlineSale("ON-2", Utc(9, 5), total: 5m, routeCustomer: null);
        await _context.SaveChangesAsync();

        var rows = Usd(await AnalyseAsync()).ByBusinessPartner;

        Assert.Equal(
            new[] { ("CORNER1", "CORNER1 Store", 60m), ("TUCK01", "TUCK01 Store", 50m), ("", "No customer recorded", 5m) },
            rows.Select(row => (row.Key, row.Label, row.TotalAmount)));
    }

    /// <summary>
    /// An offline sale is uploaded in a batch, often hours after it was made, so its hour is the signed
    /// receipt's. An online one is requested as it is made, and its instant is UTC.
    /// </summary>
    [Fact]
    public async Task Hours_are_when_the_sale_was_made_on_the_CAT_clock()
    {
        AddOfflineSale("OFF-1", total: 40m, receiptDate: Day.AddHours(8).AddMinutes(15));
        AddOnlineSale("ON-1", Utc(9, 0), total: 60m);
        await _context.SaveChangesAsync();

        var hours = Usd(await AnalyseAsync()).ByHour;

        Assert.Equal(new[] { (8, 40m), (11, 60m) }, hours.Select(hour => (hour.Hour, hour.TotalAmount)));
    }

    [Fact]
    public async Task A_payment_method_confines_every_figure()
    {
        AddOfflineSale("OFF-1", total: 40m, paymentMethod: "Cash");
        AddOfflineSale("OFF-2", total: 15m, paymentMethod: "ecocash", paymentReference: null);
        AddOnlineSale("ON-1", Utc(9, 0), total: 60m, paymentMethod: "Ecocash");
        AddOnlineSale("ON-2", Utc(9, 10), total: 7m, paymentMethod: null);
        await _context.SaveChangesAsync();

        var all = await AnalyseAsync();
        Assert.Equal(new[] { "Cash", "Ecocash", "Innbucks", "Not recorded" }, all.PaymentMethods);

        var wallet = Usd(all).ByPaymentMethod.Single(row => row.PaymentMethod == "Ecocash");
        Assert.Equal(75m, wallet.TotalAmount);
        // Only the offline sale had anywhere to record a reference.
        Assert.Equal(1, wallet.WithoutReferenceCount);

        var ecocash = Usd(await AnalyseAsync(paymentMethod: "Ecocash"));
        Assert.Equal(2, ecocash.SalesCount);
        Assert.Equal(75m, ecocash.TotalAmount);
    }

    [Fact]
    public async Task The_previous_period_is_read_under_the_same_filters()
    {
        AddOfflineSale("OFF-1", total: 40m);
        AddOfflineSale("OLD-1", total: 30m, docDate: Day.AddDays(-1));
        AddOnlineSale("OLD-2", Utc(9, 0).AddDays(-1), total: 20m, warehouse: OtherVan);
        await _context.SaveChangesAsync();

        var usd = Usd(await AnalyseAsync(warehouse: Van));

        Assert.Equal(1, usd.PreviousSalesCount);
        Assert.Equal(30m, usd.PreviousTotalAmount);
    }

    [Fact]
    public async Task A_period_that_ends_before_it_starts_is_refused()
    {
        var result = await new GetVanSalesAnalysisHandler(_context).Handle(
            new GetVanSalesAnalysisQuery(Day, Day.AddDays(-1)), CancellationToken.None);

        Assert.True(result.IsError);
    }

    private async Task<DesktopSalesAnalysisResult> AnalyseAsync(string? warehouse = null, string? paymentMethod = null)
    {
        var result = await new GetVanSalesAnalysisHandler(_context).Handle(
            new GetVanSalesAnalysisQuery(Day, Day, warehouse, paymentMethod), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private static DesktopSalesCurrencyAnalysis Usd(DesktopSalesAnalysisResult result) =>
        result.Currencies.Single(section => section.Currency == "USD");

    private static DateTime Utc(int hour, int minute) =>
        new(Day.Year, Day.Month, Day.Day, hour, minute, 0, DateTimeKind.Utc);

    private void AddOfflineSale(
        string reference,
        decimal total,
        decimal vat = 0m,
        DateTime? docDate = null,
        string? routeCustomer = "TUCK01",
        string sourceSystem = "KefalosVanSales",
        string? paymentMethod = "Cash",
        string? paymentReference = null,
        DateTime? receiptDate = null)
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = sourceSystem,
            CardCode = VanAccount,
            CardName = "Van 010",
            RouteCustomerCode = routeCustomer,
            RouteCustomerName = routeCustomer is null ? null : $"{routeCustomer} Store",
            DocDate = docDate ?? Day,
            TotalAmount = total,
            VatAmount = vat,
            Currency = "USD",
            WarehouseCode = Van,
            PaymentMethod = paymentMethod,
            PaymentReference = paymentReference,
            ReceiptDate = receiptDate,
            AmountPaid = total,
            CreatedBy = Rep.ToString(),
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = "CHE011",
                    ItemDescription = "Cheddar 1kg",
                    Quantity = 1m,
                    UnitPrice = total,
                    LineTotal = total,
                    WarehouseCode = Van,
                    UoMCode = "EA"
                }
            ]
        });
    }

    private void AddOnlineSale(
        string reference,
        DateTime createdAtUtc,
        decimal total,
        string warehouse = Van,
        string? routeCustomer = "CORNER1",
        string? paymentMethod = "Cash")
    {
        _context.StockReservations.Add(new StockReservationEntity
        {
            ReservationId = Guid.NewGuid().ToString(),
            ExternalReferenceId = reference,
            SourceSystem = "KefalosVanSales",
            DocumentType = ReservationDocumentType.Invoice,
            CardCode = VanAccount,
            CardName = "Van 010",
            RouteCustomerCode = routeCustomer,
            RouteCustomerName = routeCustomer is null ? null : $"{routeCustomer} Store",
            TotalValue = total,
            Currency = "USD",
            PaymentMethod = paymentMethod,
            Status = ReservationStatus.Confirmed,
            CreatedAt = createdAtUtc,
            ExpiresAt = createdAtUtc.AddHours(1),
            ConfirmedAt = createdAtUtc,
            CreatedBy = Rep.ToString(),
            Lines =
            [
                new StockReservationLineEntity
                {
                    LineNum = 0,
                    ItemCode = "CHE011",
                    ItemDescription = "Cheddar 1kg",
                    OriginalQuantity = 1m,
                    ReservedQuantity = 1m,
                    UoMCode = "EA",
                    WarehouseCode = warehouse,
                    UnitPrice = total,
                    LineTotal = total
                }
            ]
        });
    }
}
