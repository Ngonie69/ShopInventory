using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the shared van sales fact stream — the one place the union, the two clocks and the rep
/// attribution are solved.
///
/// Every case below is a wrong answer that produces no error. A sale read from one table only, an
/// evening sale filed a day early, a rep whose whole day vanishes because an id would not parse, a
/// shop invented out of a blank column: none of them throw, none of them show up as an empty result,
/// and all of them are simply a smaller number than the truth. That is why they are tested here once
/// rather than in each report that will read this.
/// </summary>
public sealed class VanSalesFactReaderTests : IDisposable
{
    private const string VanWarehouse = "VAN010";
    private const string VanAccount = "VAN010";

    private static readonly Guid Rep = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherRep = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>The trading day every case below is written around.</summary>
    private static readonly DateTime Day = new(2026, 8, 10);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesFactReaderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // --- The union ---

    /// <summary>
    /// The reason this class exists. A van sale lands in one of two tables depending only on whether
    /// the handset had signal, and a report reading either alone under-reports in silence.
    /// </summary>
    [Fact]
    public async Task Both_tables_are_read_as_one_stream()
    {
        AddOfflineSale("OFF-1", Day, "TUCK01", total: 40m);
        AddOnlineSale("ON-1", Utc(Day, 9, 0), "CORNER1", total: 60m);
        await _context.SaveChangesAsync();

        var facts = await LoadAsync();

        Assert.Equal(2, facts.Count);
        Assert.Equal(100m, facts.Sum(fact => fact.TotalAmount));
        Assert.Contains(facts, fact => fact.Source == VanSaleSource.OfflineBatch);
        Assert.Contains(facts, fact => fact.Source == VanSaleSource.OnlineInvoice);
    }

    /// <summary>
    /// Only a confirmed reservation means an invoice was posted. A pending one is stock held for a
    /// sale that may never happen, and a cancelled one is a sale that did not.
    /// </summary>
    [Theory]
    [InlineData(ReservationStatus.Pending)]
    [InlineData(ReservationStatus.Cancelled)]
    [InlineData(ReservationStatus.Expired)]
    public async Task An_unconfirmed_reservation_is_not_a_sale(string status)
    {
        AddOnlineSale("ON-1", Utc(Day, 9, 0), "TUCK01", total: 60m, status: status);
        await _context.SaveChangesAsync();

        Assert.Empty(await LoadAsync());
    }

    /// <summary>A sale from the shop till is not a van sale, however it is dated.</summary>
    [Fact]
    public async Task Sales_from_another_source_system_are_left_alone()
    {
        AddOfflineSale("TILL-1", Day, "TUCK01", total: 40m, sourceSystem: "KefalosShopTill");
        await _context.SaveChangesAsync();

        Assert.Empty(await LoadAsync());
    }

    // --- The two clocks ---

    /// <summary>
    /// The trap this whole class is built around. A reservation stores a UTC instant, and CAT is two
    /// hours ahead — so a sale made at half past ten at night is still that day's takings, while one
    /// made half an hour after midnight belongs to the next day. Reading <c>CreatedAt.Date</c>
    /// instead would put both on the earlier day and quietly move an evening's money.
    /// </summary>
    [Fact]
    public async Task An_evening_sale_stays_on_the_day_it_was_made()
    {
        // 20:30 UTC is 22:30 CAT on the 10th — the last of that day's trade.
        AddOnlineSale("ON-LATE", Utc(Day, 20, 30), "TUCK01", total: 25m);

        // 22:30 UTC is 00:30 CAT on the 11th — the next trading day, not this one.
        AddOnlineSale("ON-MIDNIGHT", Utc(Day, 22, 30), "TUCK01", total: 99m);

        await _context.SaveChangesAsync();

        var facts = await LoadAsync(Day, Day);

        var fact = Assert.Single(facts);
        Assert.Equal("ON-LATE", fact.ExternalReferenceId);
        Assert.Equal(Day, fact.TradingDate);
    }

    /// <summary>The next day's window picks up exactly what the previous day's did not.</summary>
    [Fact]
    public async Task The_sale_just_after_midnight_lands_on_the_following_day()
    {
        AddOnlineSale("ON-MIDNIGHT", Utc(Day, 22, 30), "TUCK01", total: 99m);
        await _context.SaveChangesAsync();

        var fact = Assert.Single(await LoadAsync(Day.AddDays(1), Day.AddDays(1)));

        Assert.Equal(Day.AddDays(1), fact.TradingDate);
    }

    /// <summary>
    /// An offline sale already carries a bare CAT trading day, so it needs no conversion — and must
    /// not be given one, or it would move in the opposite direction to the reservation beside it.
    /// </summary>
    [Fact]
    public async Task An_offline_sale_keeps_the_trading_day_the_handset_stated()
    {
        AddOfflineSale("OFF-1", Day, "TUCK01", total: 40m);
        await _context.SaveChangesAsync();

        var fact = Assert.Single(await LoadAsync(Day, Day));

        Assert.Equal(Day, fact.TradingDate);
    }

    // --- Attribution ---

    /// <summary>
    /// <c>CreatedBy</c> holds a user id as text. Anything else came from somewhere this report cannot
    /// speak for, and is dropped rather than guessed at — but it must not take the rest of the batch
    /// down with it.
    /// </summary>
    [Fact]
    public async Task A_sale_whose_rep_cannot_be_resolved_is_skipped_not_fatal()
    {
        AddOfflineSale("OFF-GOOD", Day, "TUCK01", total: 40m);
        AddOfflineSale("OFF-BAD", Day, "TUCK01", total: 500m, createdBy: "system");
        await _context.SaveChangesAsync();

        var fact = Assert.Single(await LoadAsync());

        Assert.Equal("OFF-GOOD", fact.ExternalReferenceId);
        Assert.Equal(Rep, fact.UserId);
    }

    /// <summary>Asking for one rep must not return the van parked next to them.</summary>
    [Fact]
    public async Task One_reps_window_holds_only_their_own_sales()
    {
        AddOfflineSale("OFF-MINE", Day, "TUCK01", total: 40m);
        AddOfflineSale("OFF-THEIRS", Day, "TUCK01", total: 70m, createdBy: OtherRep.ToString());
        await _context.SaveChangesAsync();

        var fact = Assert.Single(await LoadAsync(userId: Rep));

        Assert.Equal("OFF-MINE", fact.ExternalReferenceId);
    }

    /// <summary>
    /// The account on the document is the van's own, so a sale with no route customer cannot be shown
    /// to belong to any shop. It has to stay null all the way to the report, which is what lets the
    /// page label it unattributed instead of crediting a shop that never bought.
    /// </summary>
    [Fact]
    public async Task A_sale_with_no_shop_on_it_reports_no_shop()
    {
        AddOfflineSale("OFF-1", Day, routeCustomerCode: null, total: 40m);

        // Blank rather than absent — the other way this arrives, and the one a null check misses.
        AddOfflineSale("OFF-2", Day, routeCustomerCode: "   ", total: 10m);

        await _context.SaveChangesAsync();

        var facts = await LoadAsync();

        Assert.Equal(2, facts.Count);
        Assert.All(facts, fact => Assert.Null(fact.RouteCustomerCode));
    }

    // --- Line grain ---

    /// <summary>
    /// The two tables do not agree on what a quantity column means. A reservation stores what the rep
    /// asked for and what that converted to in the inventory unit; an offline sale stores only the
    /// first. Reporting the wrong one adds cases to eaches without ever looking wrong.
    /// </summary>
    [Fact]
    public async Task A_reservation_line_reports_the_sold_quantity_and_keeps_the_inventory_one()
    {
        AddOnlineSale(
            "ON-1",
            Utc(Day, 9, 0),
            "TUCK01",
            total: 60m,
            soldQuantity: 2m,
            inventoryQuantity: 24m,
            uom: "CASE");

        await _context.SaveChangesAsync();

        var line = Assert.Single(await LoadLinesAsync());

        // Two cases sold, which is what the price was struck on.
        Assert.Equal(2m, line.Quantity);
        Assert.Equal("CASE", line.UoMCode);

        // Twenty-four units left the van, which is what stock has to reconcile against.
        Assert.Equal(24m, line.InventoryQuantity);
    }

    /// <summary>
    /// The offline path records one quantity only. Null says "not recorded"; a zero here would read
    /// as a line that moved no stock and would silently under-count a reconciliation.
    /// </summary>
    [Fact]
    public async Task An_offline_line_has_no_inventory_quantity_rather_than_a_zero_one()
    {
        AddOfflineSale("OFF-1", Day, "TUCK01", total: 40m, soldQuantity: 4m, uom: "EA");
        await _context.SaveChangesAsync();

        var line = Assert.Single(await LoadLinesAsync());

        Assert.Equal(4m, line.Quantity);
        Assert.Null(line.InventoryQuantity);
    }

    /// <summary>
    /// The discount is on every line and has never been read. It is the whole basis of price
    /// realisation reporting, so it has to survive the trip out of both tables.
    /// </summary>
    [Fact]
    public async Task The_discount_given_at_the_door_survives_to_the_line()
    {
        AddOfflineSale("OFF-1", Day, "TUCK01", total: 40m, discountPercent: 12.5m);
        AddOnlineSale("ON-1", Utc(Day, 9, 0), "CORNER1", total: 60m, discountPercent: 7.5m);
        await _context.SaveChangesAsync();

        var lines = await LoadLinesAsync();

        Assert.Equal(12.5m, lines.Single(line => line.Source == VanSaleSource.OfflineBatch).DiscountPercent);
        Assert.Equal(7.5m, lines.Single(line => line.Source == VanSaleSource.OnlineInvoice).DiscountPercent);
    }

    /// <summary>Lines inherit the day and the shop from the sale they belong to.</summary>
    [Fact]
    public async Task Lines_carry_the_trading_day_and_shop_of_their_sale()
    {
        AddOnlineSale("ON-LATE", Utc(Day, 20, 30), "TUCK01", total: 25m);
        await _context.SaveChangesAsync();

        var line = Assert.Single(await LoadLinesAsync(Day, Day));

        Assert.Equal(Day, line.TradingDate);
        Assert.Equal("TUCK01", line.RouteCustomerCode);
        Assert.Equal(Rep, line.UserId);
    }

    // --- Currency ---

    /// <summary>
    /// USD and ZiG are held apart everywhere in this system. The stream keeps each sale's own
    /// currency so a caller cannot accidentally be handed one number covering both.
    /// </summary>
    [Fact]
    public async Task Each_sale_keeps_its_own_currency()
    {
        AddOfflineSale("OFF-USD", Day, "TUCK01", total: 40m, currency: "USD");
        AddOfflineSale("OFF-ZWG", Day, "TUCK01", total: 900m, currency: "ZWG");
        await _context.SaveChangesAsync();

        var facts = await LoadAsync();

        Assert.Equal("USD", facts.Single(fact => fact.ExternalReferenceId == "OFF-USD").Currency);
        Assert.Equal("ZWG", facts.Single(fact => fact.ExternalReferenceId == "OFF-ZWG").Currency);
    }

    // --- Tender classification ---

    /// <summary>
    /// ZIMRA calls both mobile wallets the same thing, so the split has to be made on the brand the
    /// handset sends. The one-n spelling is in the field on handsets that are still uploading.
    /// </summary>
    [Theory]
    [InlineData("Cash", VanSalesTender.Cash)]
    [InlineData("cash", VanSalesTender.Cash)]
    [InlineData("EcoCash", VanSalesTender.Ecocash)]
    [InlineData("Innbucks", VanSalesTender.Innbucks)]
    [InlineData("inbucks", VanSalesTender.Innbucks)]
    // A swipe is a named tender the departure sheet has no column for, which is a different thing
    // from a sale that named none at all — see the test below.
    [InlineData("Swipe", VanSalesTender.Other)]
    public void A_tender_is_classified_by_the_brand_the_handset_sent(string method, VanSalesTender expected)
    {
        Assert.Equal(expected, VanSalesFacts.ClassifyTender(method));
    }

    /// <summary>
    /// A handset built before the payment picker names no tender. Unallocated is the honest answer;
    /// defaulting it to cash would make a real declaration variance disappear.
    /// </summary>
    /// <remarks>
    /// <see cref="VanSalesTender.Untendered"/> rather than <see cref="VanSalesTender.Other"/>, because
    /// the departure sheet's cash variance turns on the difference: a swipe is money the rep certainly
    /// did not count into the pouch, while this may well be cash they did. Collapsing the two makes
    /// the sheet either accuse an honest rep or excuse a real overage.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_sale_naming_no_tender_is_unallocated_rather_than_cash(string? method)
    {
        Assert.Equal(VanSalesTender.Untendered, VanSalesFacts.ClassifyTender(method));
    }

    // --- The window ---

    /// <summary>
    /// Half-open at the top, because the last moment of a CAT trading day is 21:59:59.999… UTC and no
    /// inclusive bound expresses that without a sentinel the two databases would have to agree on.
    /// </summary>
    [Fact]
    public void The_utc_window_covers_the_cat_day_and_stops_where_the_next_one_starts()
    {
        var (fromUtc, toUtcExclusive) = VanSalesFacts.ToUtcWindow(Day, Day);

        Assert.Equal(new DateTime(2026, 8, 9, 22, 0, 0), fromUtc);
        Assert.Equal(new DateTime(2026, 8, 10, 22, 0, 0), toUtcExclusive);
    }

    // --- Helpers ---

    private Task<List<VanSaleFact>> LoadAsync(
        DateTime? from = null,
        DateTime? to = null,
        Guid? userId = null) =>
        VanSalesFactReader.LoadSalesAsync(
            _context,
            new VanSalesFactFilter(from ?? Day, to ?? Day, userId),
            CancellationToken.None);

    private Task<List<VanSaleLineFact>> LoadLinesAsync(DateTime? from = null, DateTime? to = null) =>
        VanSalesFactReader.LoadSaleLinesAsync(
            _context,
            new VanSalesFactFilter(from ?? Day, to ?? Day),
            CancellationToken.None);

    private static DateTime Utc(DateTime day, int hour, int minute) =>
        new(day.Year, day.Month, day.Day, hour, minute, 0, DateTimeKind.Utc);

    private void AddOfflineSale(
        string reference,
        DateTime docDate,
        string? routeCustomerCode,
        decimal total,
        string? createdBy = null,
        string sourceSystem = "KefalosVanSales",
        string currency = "USD",
        string? paymentMethod = "Cash",
        decimal soldQuantity = 1m,
        decimal discountPercent = 0m,
        string uom = "EA")
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = sourceSystem,
            CardCode = VanAccount,
            CardName = "Van 010",
            RouteCustomerCode = routeCustomerCode,
            RouteCustomerName = routeCustomerCode is null ? null : "Tuck Shop",
            DocDate = docDate,
            TotalAmount = total,
            VatAmount = 0m,
            Currency = currency,
            WarehouseCode = VanWarehouse,
            PaymentMethod = paymentMethod,
            AmountPaid = total,
            CreatedBy = createdBy ?? Rep.ToString(),
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = "CHE011",
                    ItemDescription = "Cheddar 1kg",
                    Quantity = soldQuantity,
                    UnitPrice = total / soldQuantity,
                    LineTotal = total,
                    WarehouseCode = VanWarehouse,
                    DiscountPercent = discountPercent,
                    UoMCode = uom
                }
            ]
        });
    }

    private void AddOnlineSale(
        string reference,
        DateTime createdAtUtc,
        string? routeCustomerCode,
        decimal total,
        string status = ReservationStatus.Confirmed,
        string? createdBy = null,
        string currency = "USD",
        decimal soldQuantity = 1m,
        decimal? inventoryQuantity = null,
        decimal discountPercent = 0m,
        string uom = "EA")
    {
        _context.StockReservations.Add(new StockReservationEntity
        {
            ReservationId = Guid.NewGuid().ToString(),
            ExternalReferenceId = reference,
            SourceSystem = "KefalosVanSales",
            DocumentType = ReservationDocumentType.Invoice,
            CardCode = VanAccount,
            CardName = "Van 010",
            RouteCustomerCode = routeCustomerCode,
            RouteCustomerName = routeCustomerCode is null ? null : "Corner Store",
            TotalValue = total,
            Currency = currency,
            Status = status,
            CreatedAt = createdAtUtc,
            ExpiresAt = createdAtUtc.AddHours(1),
            ConfirmedAt = status == ReservationStatus.Confirmed ? createdAtUtc : null,
            CreatedBy = createdBy ?? Rep.ToString(),
            Lines =
            [
                new StockReservationLineEntity
                {
                    LineNum = 0,
                    ItemCode = "CHE011",
                    ItemDescription = "Cheddar 1kg",
                    OriginalQuantity = soldQuantity,
                    ReservedQuantity = inventoryQuantity ?? soldQuantity,
                    UoMCode = uom,
                    WarehouseCode = VanWarehouse,
                    UnitPrice = total / soldQuantity,
                    LineTotal = total,
                    DiscountPercent = discountPercent
                }
            ]
        });
    }
}
