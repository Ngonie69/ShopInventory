using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries.GetDepartureComplianceReport;
using ShopInventory.Features.VanSalesReports.Queries.GetVanSalesPerformanceReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the van sales performance report.
///
/// The report's whole claim is that it agrees with the compliance report, because both read the same
/// fact stream — so the first test here holds the two against each other on the same window. If that
/// one ever fails, the two pages are telling a manager different things about the same day and there
/// is no way for them to tell which is right.
///
/// The rest guard the joins and the divisions, which is where every remaining wrong answer lives: a
/// route that came from the wrong place, a currency folded into another, a null read as a zero.
/// </summary>
public sealed class VanSalesPerformanceReportTests : IDisposable
{
    private const string VanWarehouse = "VAN010";
    private const string VanAccount = "VAN010";

    private static readonly Guid Rep = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid OtherRep = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTime Day = new(2026, 8, 10);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesPerformanceReportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();

        AddUser(Rep, "van010", "Tinashe", "Moyo");
        AddUser(OtherRep, "van011", "Rudo", "Chikanga");
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // --- The claim that matters ---

    /// <summary>
    /// Both reports read the same fact stream, so their gross takings and their productive calls must
    /// be the same number. Two pages disagreeing about a day is worse than one page missing.
    /// </summary>
    [Fact]
    public async Task It_agrees_with_the_compliance_report_on_the_same_window()
    {
        AddRouteDay(Rep, planned: 10, routeCode: "GURUVE", territory: "Mash Central");
        AddVisit(Rep, "TUCK01", Utc(8, 30));
        AddVisit(Rep, "CORNER1", Utc(11, 0));
        AddOfflineSale(Rep, "OFF-1", "TUCK01", total: 40m);
        AddOnlineSale(Rep, "ON-1", Utc(9, 0), "CORNER1", total: 60m);
        AddOfflineSale(Rep, "OFF-2", routeCustomerCode: null, total: 15m);
        await _context.SaveChangesAsync();

        var performance = await RunAsync();

        var compliance = await new GetDepartureComplianceReportHandler(_context).Handle(
            new GetDepartureComplianceReportQuery(Day, Day),
            CancellationToken.None);

        Assert.False(compliance.IsError);

        var usd = Assert.Single(performance.Summary.TotalsByCurrency);
        Assert.Equal(compliance.Value.Summary.TotalSales, usd.Gross);
        Assert.Equal(compliance.Value.Summary.ProductiveCalls, performance.Summary.ProductiveCalls);
    }

    // --- The route join ---

    /// <summary>
    /// Sales carry no route. The only thing that says which route a day was worked is the departure
    /// record, joined on the rep and the trading day.
    /// </summary>
    [Fact]
    public async Task Sales_take_their_route_from_the_departure_record()
    {
        AddRouteDay(Rep, planned: 10, routeCode: "GURUVE", territory: "Mash Central");
        AddOfflineSale(Rep, "OFF-1", "TUCK01", total: 40m);
        await _context.SaveChangesAsync();

        var route = Assert.Single((await RunAsync()).Routes);

        Assert.True(route.HasRouteDay);
        Assert.Equal("GURUVE", route.RouteCode);
        Assert.Equal("Mash Central", route.Territory);
        Assert.Equal(40m, Assert.Single(route.TotalsByCurrency).Gross);
    }

    /// <summary>
    /// A rep who traded without opening a day cannot be shown to have been anywhere. The sale is kept
    /// — the money is real — but it goes in its own bucket, sorted last, never among the routes.
    /// </summary>
    [Fact]
    public async Task A_sale_with_no_departure_record_is_its_own_bucket_not_a_route()
    {
        AddRouteDay(Rep, planned: 10, routeCode: "GURUVE", territory: "Mash Central");
        AddOfflineSale(Rep, "OFF-1", "TUCK01", total: 40m);
        AddOfflineSale(OtherRep, "OFF-2", "SHOP9", total: 25m);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(2, report.Routes.Count);

        var orphan = report.Routes[^1];
        Assert.False(orphan.HasRouteDay);
        Assert.Null(orphan.RouteCode);
        Assert.Null(orphan.Territory);
        Assert.Null(orphan.PlannedCalls);
        Assert.Equal(25m, Assert.Single(orphan.TotalsByCurrency).Gross);

        // It is not counted as a route in the summary either.
        Assert.Equal(1, report.Summary.RouteCount);
        Assert.Equal(1, report.Coverage.SalesWithoutRouteDay);
    }

    /// <summary>
    /// Asking for one route must not be answered by a sale that cannot be shown to belong to it. Same
    /// rule as the compliance report, and for the same reason.
    /// </summary>
    [Fact]
    public async Task A_route_filter_excludes_sales_with_no_departure_record()
    {
        AddRouteDay(Rep, planned: 10, routeCode: "GURUVE", territory: "Mash Central");
        AddOfflineSale(Rep, "OFF-1", "TUCK01", total: 40m);
        AddOfflineSale(OtherRep, "OFF-2", "SHOP9", total: 25m);
        await _context.SaveChangesAsync();

        var report = await RunAsync(routeCode: "GURUVE");

        var route = Assert.Single(report.Routes);
        Assert.Equal("GURUVE", route.RouteCode);
        Assert.Equal(40m, Assert.Single(report.Summary.TotalsByCurrency).Gross);
    }

    /// <summary>
    /// A day opened before its route was named is a different finding from a day never opened, and the
    /// two must not share a row.
    /// </summary>
    [Fact]
    public async Task A_day_opened_without_a_route_is_kept_apart_from_a_day_never_opened()
    {
        AddRouteDay(Rep, planned: 10, routeCode: null, territory: null);
        AddOfflineSale(Rep, "OFF-1", "TUCK01", total: 40m);
        AddOfflineSale(OtherRep, "OFF-2", "SHOP9", total: 25m);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        var opened = report.Routes.Single(route => route.HasRouteDay);
        Assert.Null(opened.RouteCode);
        Assert.Equal(10, opened.PlannedCalls);

        var never = report.Routes.Single(route => !route.HasRouteDay);
        Assert.Null(never.PlannedCalls);
    }

    // --- Rates that must not become zeros ---

    /// <summary>
    /// A rep with sales and no visit records is not a rep with a 0% strike rate. He is one whose calls
    /// were never recorded, and saying 0% would be a slander dressed as a measurement.
    /// </summary>
    [Fact]
    public async Task A_rep_with_no_visit_records_has_no_strike_rate_rather_than_zero()
    {
        AddRouteDay(Rep, planned: 10, routeCode: "GURUVE", territory: "Mash Central");
        AddOfflineSale(Rep, "OFF-1", "TUCK01", total: 40m);
        await _context.SaveChangesAsync();

        var rep = Assert.Single((await RunAsync()).Reps);

        Assert.Null(rep.Calls);
        Assert.Null(rep.StrikeRate);
        Assert.Equal(1, rep.ProductiveCalls);
        Assert.Equal(1, (await RunAsync()).Coverage.RepsWithoutVisitData);
    }

    /// <summary>Nothing planned means nothing can have been missed — a null rate, not a zero one.</summary>
    [Fact]
    public async Task A_route_with_no_plan_has_no_call_compliance_rate()
    {
        AddRouteDay(Rep, planned: 0, routeCode: "GURUVE", territory: "Mash Central");
        AddVisit(Rep, "TUCK01", Utc(8, 30));
        AddOfflineSale(Rep, "OFF-1", "TUCK01", total: 40m);
        await _context.SaveChangesAsync();

        var route = Assert.Single((await RunAsync()).Routes);

        Assert.Equal(0, route.PlannedCalls);
        Assert.Null(route.CallComplianceRate);
        Assert.Equal(1.0, route.ProductiveCallRate);
    }

    /// <summary>
    /// A strike rate above 100% means sales exist with no recorded visit. That is a real discrepancy
    /// and clamping it would hide the very thing worth looking at.
    /// </summary>
    [Fact]
    public async Task A_strike_rate_over_one_is_reported_rather_than_clamped()
    {
        AddRouteDay(Rep, planned: 10, routeCode: "GURUVE", territory: "Mash Central");
        AddVisit(Rep, "TUCK01", Utc(8, 30));
        AddOfflineSale(Rep, "OFF-1", "TUCK01", total: 40m);
        AddOfflineSale(Rep, "OFF-2", "NEVER-VISITED", total: 20m);
        await _context.SaveChangesAsync();

        var rep = Assert.Single((await RunAsync()).Reps);

        Assert.Equal(1, rep.Calls);
        Assert.Equal(2, rep.ProductiveCalls);
        Assert.Equal(2.0, rep.StrikeRate);
    }

    /// <summary>An odometer that was never read is not a van that never moved.</summary>
    [Fact]
    public async Task Distance_is_unavailable_rather_than_zero_when_no_odometer_was_read()
    {
        AddRouteDay(Rep, planned: 10, routeCode: "GURUVE", territory: "Mash Central");
        AddOfflineSale(Rep, "OFF-1", "TUCK01", total: 40m);
        await _context.SaveChangesAsync();

        Assert.Null((await RunAsync()).Summary.KilometresTravelled);
    }

    // --- Currency ---

    /// <summary>
    /// A van that took USD 40 and ZWG 900 did not take 940 of anything. Every money figure on this
    /// report is a list for exactly this reason.
    /// </summary>
    [Fact]
    public async Task Currencies_are_never_added_together()
    {
        AddRouteDay(Rep, planned: 10, routeCode: "GURUVE", territory: "Mash Central");
        AddOfflineSale(Rep, "OFF-USD", "TUCK01", total: 40m, currency: "USD");
        AddOfflineSale(Rep, "OFF-ZWG", "TUCK01", total: 900m, currency: "ZWG");
        await _context.SaveChangesAsync();

        var totals = (await RunAsync()).Summary.TotalsByCurrency;

        Assert.Equal(2, totals.Count);
        Assert.Equal(900m, totals.Single(total => total.Currency == "ZWG").Gross);
        Assert.Equal(40m, totals.Single(total => total.Currency == "USD").Gross);
    }

    /// <summary>
    /// A blank currency is read as USD by the shared rule. That is a convention, not a fact, so the
    /// rows it was applied to are counted — a wave of currency-less uploads should be visible.
    /// </summary>
    [Fact]
    public async Task A_blank_currency_is_counted_as_assumed()
    {
        AddRouteDay(Rep, planned: 10, routeCode: "GURUVE", territory: "Mash Central");
        AddOfflineSale(Rep, "OFF-1", "TUCK01", total: 40m, currency: "  ");
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal("USD", Assert.Single(report.Summary.TotalsByCurrency).Currency);
        Assert.Equal(1, report.Coverage.SalesWithAssumedCurrency);
    }

    // --- Drops ---

    /// <summary>
    /// Two invoices written at one counter are one drop, which is what a field manager means by the
    /// word. Counting documents instead would halve every drop size on a route that splits invoices.
    /// </summary>
    [Fact]
    public async Task Two_sales_to_one_shop_in_a_day_are_one_drop()
    {
        AddRouteDay(Rep, planned: 10, routeCode: "GURUVE", territory: "Mash Central");
        AddOfflineSale(Rep, "OFF-1", "TUCK01", total: 40m);
        AddOfflineSale(Rep, "OFF-2", "TUCK01", total: 60m);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        var money = Assert.Single(report.Summary.TotalsByCurrency);
        Assert.Equal(2, money.DocumentCount);
        Assert.Equal(1, money.DropCount);
        Assert.Equal(100m, money.AverageDropSize);
        Assert.Equal(50m, money.AverageDocumentValue);

        var distribution = Assert.Single(report.DropSizes);
        Assert.Equal(1, distribution.DropCount);
        Assert.Equal(100m, distribution.Median);
    }

    /// <summary>The distribution is the point — a mean alone hides the tail.</summary>
    [Fact]
    public async Task The_drop_distribution_reports_its_quartiles()
    {
        AddRouteDay(Rep, planned: 10, routeCode: "GURUVE", territory: "Mash Central");

        foreach (var (index, amount) in new[] { 4m, 8m, 12m, 400m }.Select((a, i) => (i, a)))
        {
            AddOfflineSale(Rep, $"OFF-{index}", $"SHOP{index}", total: amount);
        }

        await _context.SaveChangesAsync();

        var distribution = Assert.Single((await RunAsync()).DropSizes);

        Assert.Equal(4, distribution.DropCount);
        Assert.Equal(4m, distribution.Minimum);
        Assert.Equal(400m, distribution.Maximum);
        Assert.Equal(8m, distribution.Median);
        Assert.Equal(106m, distribution.Mean);

        // The one large drop must not be smeared across the bands.
        var top = distribution.Buckets.Single(bucket => bucket.UpperBound is null);
        Assert.Equal(1, top.DropCount);
        Assert.Equal(400m, top.Total);
    }

    // --- Items and price realisation ---

    /// <summary>
    /// Items rank on reach, not on value or quantity: value cannot be compared across currencies and
    /// quantity cannot be compared across units, so either would rank on an accident of the mix.
    /// </summary>
    [Fact]
    public async Task Items_rank_on_how_many_lines_and_shops_they_reached()
    {
        AddRouteDay(Rep, planned: 10, routeCode: "GURUVE", territory: "Mash Central");
        AddOfflineSale(Rep, "OFF-1", "TUCK01", total: 10m, itemCode: "CHE011");
        AddOfflineSale(Rep, "OFF-2", "CORNER1", total: 10m, itemCode: "CHE011");
        AddOfflineSale(Rep, "OFF-3", "TUCK01", total: 900m, itemCode: "NRI049");
        await _context.SaveChangesAsync();

        var items = (await RunAsync()).Items;

        Assert.Equal("CHE011", items[0].ItemCode);
        Assert.Equal(1, items[0].Rank);
        Assert.Equal(2, items[0].CustomerCount);
        Assert.Equal("NRI049", items[1].ItemCode);
    }

    /// <summary>
    /// The replacement for the discount report that could not be built — every van line's discount is
    /// zero because neither ingest path writes one. Under-pricing still shows, because it shows in the
    /// price actually achieved.
    /// </summary>
    [Fact]
    public async Task Price_realisation_shows_one_rep_selling_below_his_peers()
    {
        AddRouteDay(Rep, planned: 10, routeCode: "GURUVE", territory: "Mash Central");
        AddRouteDay(OtherRep, planned: 10, routeCode: "MUTOKO", territory: "Mash East");

        // Same item, same unit, same currency; one rep is letting it go at eight instead of ten.
        AddOfflineSale(Rep, "OFF-1", "TUCK01", total: 100m, itemCode: "CHE011", quantity: 10m);
        AddOfflineSale(OtherRep, "OFF-2", "SHOP9", total: 80m, itemCode: "CHE011", quantity: 10m);
        await _context.SaveChangesAsync();

        var price = Assert.Single((await RunAsync()).ItemPrices);

        Assert.Equal("CHE011", price.ItemCode);
        Assert.Equal(9m, price.WeightedAveragePrice);
        Assert.Equal(8m, price.MinUnitPrice);
        Assert.Equal(10m, price.MaxUnitPrice);

        var cheapest = price.Reps[0];
        Assert.Equal(OtherRep, cheapest.UserId);
        Assert.Equal(8m, cheapest.WeightedAveragePrice);
        Assert.Equal(-11.11m, cheapest.VarianceFromItemPercent);
    }

    /// <summary>
    /// A zero-quantity line has no achieved price. It is counted and excluded, never divided by — the
    /// division would either throw or produce an infinity dressed as a price.
    ///
    /// Only the online path can produce one, and that asymmetry is worth knowing. The database
    /// enforces <c>CK_DesktopSaleLines_Quantity_Positive</c> and
    /// <c>CK_StockReservationLines_ReservedQuantity_Positive</c>, but nothing constrains a
    /// reservation's <c>OriginalQuantity</c> — which is the column this report reads as the quantity
    /// sold. So the guard is unreachable from an offline sale and reachable from an online one.
    /// </summary>
    [Fact]
    public async Task A_zero_quantity_line_is_counted_and_left_out_of_the_price()
    {
        AddRouteDay(Rep, planned: 10, routeCode: "GURUVE", territory: "Mash Central");
        AddOfflineSale(Rep, "OFF-1", "TUCK01", total: 100m, itemCode: "CHE011", quantity: 10m);
        AddOnlineSale(
            Rep,
            "ON-1",
            Utc(9, 0),
            "TUCK01",
            total: 0m,
            itemCode: "CHE011",
            quantity: 0m,
            reservedQuantity: 1m);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(1, report.Coverage.LinesWithZeroQuantity);
        Assert.Equal(10m, Assert.Single(report.ItemPrices).WeightedAveragePrice);
    }

    /// <summary>
    /// Quantity is reported per unit of measure and never across it. Van lines record no unit at all
    /// today, so the honest answer is a single "not recorded" bucket.
    /// </summary>
    [Fact]
    public async Task Quantity_is_bucketed_by_unit_of_measure()
    {
        AddRouteDay(Rep, planned: 10, routeCode: "GURUVE", territory: "Mash Central");
        AddOfflineSale(Rep, "OFF-1", "TUCK01", total: 100m, itemCode: "CHE011", quantity: 10m);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        var quantity = Assert.Single(Assert.Single(report.Items).QuantitiesByUoM);
        Assert.Null(quantity.UoMCode);
        Assert.Equal(10m, quantity.Quantity);
        Assert.Equal(1, report.Coverage.LinesWithoutUoM);
    }

    // --- Trend ---

    /// <summary>
    /// A day with no trade is a finding. Dropping it makes the spacing of the curve say something
    /// untrue about the days either side of it.
    /// </summary>
    [Fact]
    public async Task Days_with_no_trade_are_present_and_empty_rather_than_missing()
    {
        AddRouteDay(Rep, planned: 10, routeCode: "GURUVE", territory: "Mash Central");
        AddOfflineSale(Rep, "OFF-1", "TUCK01", total: 40m);
        await _context.SaveChangesAsync();

        var trend = (await RunAsync(from: Day, to: Day.AddDays(2))).Trend;

        Assert.Equal(3, trend.Daily.Count);
        Assert.Equal(1, trend.Daily[0].DocumentCount);
        Assert.Empty(trend.Daily[1].TotalsByCurrency);
        Assert.Equal(0, trend.Daily[1].DocumentCount);
    }

    /// <summary>
    /// All seven weekdays are always present, and an average divides by the days the window contained,
    /// not by the days that traded — otherwise a weekday nobody works looks average.
    /// </summary>
    [Fact]
    public async Task Every_weekday_is_reported_with_its_calendar_and_active_day_counts()
    {
        AddRouteDay(Rep, planned: 10, routeCode: "GURUVE", territory: "Mash Central");
        AddOfflineSale(Rep, "OFF-1", "TUCK01", total: 40m);
        await _context.SaveChangesAsync();

        var trend = (await RunAsync(from: Day, to: Day.AddDays(6))).Trend;

        Assert.Equal(7, trend.DayOfWeek.Count);

        var monday = trend.DayOfWeek.Single(point => point.DayOfWeek == DayOfWeek.Monday);
        Assert.Equal(1, monday.CalendarDayCount);
        Assert.Equal(1, monday.ActiveDayCount);

        var silent = trend.DayOfWeek.Single(point => point.DayOfWeek == DayOfWeek.Tuesday);
        Assert.Equal(1, silent.CalendarDayCount);
        Assert.Equal(0, silent.ActiveDayCount);
    }

    // --- Lapsed items ---

    /// <summary>
    /// An item that sold in the previous window of equal length and has stopped is the non-mover a
    /// buyer can act on. Equal length matters: a fortnight against a quarter would call half the
    /// catalogue lapsed.
    /// </summary>
    [Fact]
    public async Task An_item_that_sold_before_and_not_now_is_reported_as_lapsed()
    {
        AddRouteDay(Rep, planned: 10, routeCode: "GURUVE", territory: "Mash Central");
        AddOfflineSale(Rep, "OFF-NOW", "TUCK01", total: 40m, itemCode: "CHE011");
        AddOfflineSale(Rep, "OFF-THEN", "TUCK01", total: 30m, itemCode: "PIC003", docDate: Day.AddDays(-1));
        await _context.SaveChangesAsync();

        var report = await RunAsync(from: Day, to: Day);

        var lapsed = Assert.Single(report.LapsedItems);
        Assert.Equal("PIC003", lapsed.ItemCode);
        Assert.Equal(1, lapsed.DaysSinceLastSale);
        Assert.DoesNotContain(report.Items, item => item.ItemCode == "PIC003");
    }

    // --- Validation ---

    [Fact]
    public async Task A_backwards_period_is_refused()
    {
        var handler = new GetVanSalesPerformanceReportHandler(_context);

        var result = await handler.Handle(
            new GetVanSalesPerformanceReportQuery(Day, Day.AddDays(-1)),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesReports.InvalidRange", result.FirstError.Code);
    }

    [Fact]
    public async Task A_period_wider_than_the_ceiling_is_refused()
    {
        var handler = new GetVanSalesPerformanceReportHandler(_context);

        var result = await handler.Handle(
            new GetVanSalesPerformanceReportQuery(Day, Day.AddDays(401)),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesReports.RangeTooWide", result.FirstError.Code);
    }

    // --- Helpers ---

    private async Task<VanSalesPerformanceReportResult> RunAsync(
        DateTime? from = null,
        DateTime? to = null,
        string? routeCode = null)
    {
        var handler = new GetVanSalesPerformanceReportHandler(_context);

        var result = await handler.Handle(
            new GetVanSalesPerformanceReportQuery(from ?? Day, to ?? Day, null, routeCode),
            CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }

    private static DateTime Utc(int hour, int minute) =>
        new(Day.Year, Day.Month, Day.Day, hour, minute, 0, DateTimeKind.Utc);

    private void AddUser(Guid id, string username, string firstName, string lastName) =>
        _context.Users.Add(new User
        {
            Id = id,
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = true,
            FirstName = firstName,
            LastName = lastName,
            AssignedWarehouseCode = VanWarehouse,
            AssignedBusinessPartnerCode = VanAccount
        });

    private void AddRouteDay(Guid userId, int planned, string? routeCode, string? territory) =>
        _context.VanRouteDays.Add(new VanRouteDayEntity
        {
            UserId = userId,
            Username = userId == Rep ? "van010" : "van011",
            TradingDate = Day,
            RouteCode = routeCode,
            RouteName = routeCode is null ? null : $"{routeCode} route",
            Territory = territory,
            DepartedAt = Utc(5, 0),
            PlannedCustomerCount = planned
        });

    private void AddVisit(Guid userId, string customerCode, DateTime checkInUtc) =>
        _context.TimesheetEntries.Add(new TimesheetEntryEntity
        {
            Channel = TimesheetChannel.VanSales,
            UserId = userId,
            Username = userId == Rep ? "van010" : "van011",
            CustomerCode = customerCode,
            CustomerName = customerCode,
            CheckInTime = checkInUtc,
            CheckOutTime = checkInUtc.AddMinutes(20)
        });

    private void AddOfflineSale(
        Guid userId,
        string reference,
        string? routeCustomerCode,
        decimal total,
        string currency = "USD",
        string itemCode = "CHE011",
        decimal quantity = 1m,
        DateTime? docDate = null) =>
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = "KefalosVanSales",
            CardCode = VanAccount,
            CardName = "Van 010",
            RouteCustomerCode = routeCustomerCode,
            RouteCustomerName = routeCustomerCode is null ? null : $"Shop {routeCustomerCode}",
            DocDate = docDate ?? Day,
            TotalAmount = total,
            VatAmount = 0m,
            Currency = currency,
            WarehouseCode = VanWarehouse,
            PaymentMethod = "Cash",
            AmountPaid = total,
            CreatedBy = userId.ToString(),
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = itemCode,
                    ItemDescription = $"Item {itemCode}",
                    Quantity = quantity,
                    UnitPrice = quantity == 0 ? 0m : total / quantity,
                    LineTotal = total,
                    WarehouseCode = VanWarehouse
                }
            ]
        });

    private void AddOnlineSale(
        Guid userId,
        string reference,
        DateTime createdAtUtc,
        string? routeCustomerCode,
        decimal total,
        string currency = "USD",
        string itemCode = "CHE011",
        decimal quantity = 1m,
        decimal? reservedQuantity = null) =>
        _context.StockReservations.Add(new StockReservationEntity
        {
            ReservationId = Guid.NewGuid().ToString(),
            ExternalReferenceId = reference,
            SourceSystem = "KefalosVanSales",
            DocumentType = ReservationDocumentType.Invoice,
            CardCode = VanAccount,
            CardName = "Van 010",
            RouteCustomerCode = routeCustomerCode,
            RouteCustomerName = routeCustomerCode is null ? null : $"Shop {routeCustomerCode}",
            TotalValue = total,
            Currency = currency,
            PaymentMethod = "Cash",
            Status = ReservationStatus.Confirmed,
            CreatedAt = createdAtUtc,
            ExpiresAt = createdAtUtc.AddHours(1),
            ConfirmedAt = createdAtUtc,
            CreatedBy = userId.ToString(),
            Lines =
            [
                new StockReservationLineEntity
                {
                    LineNum = 0,
                    ItemCode = itemCode,
                    ItemDescription = $"Item {itemCode}",
                    OriginalQuantity = quantity,
                    ReservedQuantity = reservedQuantity ?? quantity,
                    WarehouseCode = VanWarehouse,
                    UnitPrice = quantity == 0 ? 0m : total / quantity,
                    LineTotal = total
                }
            ]
        });
}
