using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries.GetDepartureComplianceReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the compliance report as a whole, rather than the arithmetic on its rows, which
/// <see cref="DepartureComplianceMetricsTests"/> already pins.
///
/// It exists because this report now reads its sales through the shared fact stream that the rest of
/// the van sales reporting will read. That is what stops two pages disagreeing about a day's takings
/// — but only while something proves the report still counts what it counted before. Nothing did:
/// the handler had no test of its own, so the extraction could have moved a figure without a single
/// red test.
/// </summary>
public sealed class DepartureComplianceReportTests : IDisposable
{
    private const string VanWarehouse = "VAN010";
    private const string VanAccount = "VAN010";
    private const string RouteCode = "GURUVE";

    private static readonly Guid Rep = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime Day = new(2026, 8, 10);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public DepartureComplianceReportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = Rep,
            Username = "van010",
            Email = "van010@example.com",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = true,
            FirstName = "Tinashe",
            LastName = "Moyo",
            AssignedWarehouseCode = VanWarehouse,
            AssignedBusinessPartnerCode = VanAccount
        });

        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// The figure the whole sheet hangs on. Both tables have to reach it, or a day's takings read
    /// short by exactly the sales the handset happened to make while it had signal.
    /// </summary>
    [Fact]
    public async Task A_days_takings_include_both_kinds_of_sale()
    {
        AddDay(plannedCustomers: 10);
        AddOfflineSale("OFF-1", "TUCK01", total: 40m);
        AddOnlineSale("ON-1", Utc(9, 0), "CORNER1", total: 60m);
        await _context.SaveChangesAsync();

        var day = await SingleDayAsync();

        Assert.Equal(100m, day.SystemTotalSales);
    }

    /// <summary>
    /// A shop that took an offline sale and an online invoice on the same day is one productive call,
    /// not two. Counting the tables separately inflates the strike rate, and a rate above the number
    /// of calls made is the kind of wrong that gets a report quietly ignored.
    /// </summary>
    [Fact]
    public async Task One_shop_buying_twice_in_a_day_is_one_productive_call()
    {
        AddDay(plannedCustomers: 10);
        AddVisit("TUCK01", Utc(8, 30));
        AddOfflineSale("OFF-1", "TUCK01", total: 40m);
        AddOnlineSale("ON-1", Utc(9, 0), "TUCK01", total: 60m);
        await _context.SaveChangesAsync();

        var day = await SingleDayAsync();

        Assert.Equal(1, day.CustomersVisited);
        Assert.Equal(1, day.ProductiveCalls);
        Assert.Equal(1.0, day.ProductiveCallRate);
    }

    /// <summary>
    /// Sales with no shop on them are one bucket, not one shop each — otherwise the day a handset
    /// stopped reporting shops becomes the busiest the route has ever had.
    /// </summary>
    [Fact]
    public async Task Unattributed_sales_count_as_a_single_productive_call_between_them()
    {
        AddDay(plannedCustomers: 10);
        AddOfflineSale("OFF-1", routeCustomerCode: null, total: 40m);
        AddOfflineSale("OFF-2", routeCustomerCode: null, total: 10m);
        AddOfflineSale("OFF-3", routeCustomerCode: null, total: 15m);
        await _context.SaveChangesAsync();

        var day = await SingleDayAsync();

        Assert.Equal(1, day.ProductiveCalls);
        Assert.Equal(65m, day.SystemTotalSales);
    }

    /// <summary>
    /// The cash split is the point of the sheet. An online sale carries no tender on older handsets
    /// and lands in the unallocated column rather than being counted as cash, which is what keeps a
    /// declaration variance visible instead of balancing it by accident.
    /// </summary>
    [Fact]
    public async Task Takings_land_in_the_tender_column_the_handset_named()
    {
        AddDay(plannedCustomers: 10);
        AddOfflineSale("OFF-CASH", "TUCK01", total: 40m, paymentMethod: "Cash");
        AddOfflineSale("OFF-ECO", "TUCK01", total: 25m, paymentMethod: "EcoCash");
        AddOfflineSale("OFF-INN", "TUCK01", total: 15m, paymentMethod: "Innbucks");
        AddOnlineSale("ON-NONE", Utc(9, 0), "CORNER1", total: 20m, paymentMethod: null);
        await _context.SaveChangesAsync();

        var day = await SingleDayAsync();

        Assert.Equal(40m, day.SystemCash);
        Assert.Equal(25m, day.SystemEcocash);
        Assert.Equal(15m, day.SystemInnbucks);
        Assert.Equal(20m, day.SystemOther);
        Assert.Equal(100m, day.SystemTotalSales);
    }

    /// <summary>
    /// An online sale's tender was being thrown away.
    ///
    /// <c>StockReservations.PaymentMethod</c> was added in the same commit as this report and for this
    /// column of it, and the handset fills it — but the report hard-coded null with a comment saying
    /// reservations have no payment column, which had stopped being true. Every online sale therefore
    /// landed in the unallocated column, so a rep who sold online and declared honestly showed a cash
    /// variance that was not real. Fixed on 2026-08-17; this is the test that says so.
    /// </summary>
    [Fact]
    public async Task An_online_sales_tender_is_read_rather_than_treated_as_unallocated()
    {
        AddDay(plannedCustomers: 10);
        AddOnlineSale("ON-CASH", Utc(9, 0), "TUCK01", total: 30m, paymentMethod: "Cash");
        AddOnlineSale("ON-ECO", Utc(10, 0), "CORNER1", total: 45m, paymentMethod: "EcoCash");
        await _context.SaveChangesAsync();

        var day = await SingleDayAsync();

        Assert.Equal(30m, day.SystemCash);
        Assert.Equal(45m, day.SystemEcocash);
        Assert.Equal(0m, day.SystemOther);
    }

    /// <summary>
    /// The defect this pins: a day's money used to be summed across every currency and stamped with
    /// whichever one the first sale happened to carry.
    ///
    /// The row exists to be reconciled against a till the rep counted in one currency, so the system
    /// figure has to be in that same one. A rep taking USD 100 and ZWG 3,000 and declaring the USD
    /// correctly was shown a USD 3,000 shortfall on a cash-control report.
    /// </summary>
    [Fact]
    public async Task A_days_money_is_stated_in_the_currency_the_rep_declared_in()
    {
        AddDay(plannedCustomers: 10, declaredCash: 100m);
        AddOfflineSale("OFF-USD", "TUCK01", total: 100m, paymentMethod: "Cash", currency: "USD");
        AddOfflineSale("OFF-ZWG", "CORNER1", total: 3000m, paymentMethod: "Cash", currency: "ZWG");
        await _context.SaveChangesAsync();

        var day = await SingleDayAsync();

        Assert.Equal("USD", day.Currency);
        Assert.Equal(100m, day.SystemTotalSales);
        Assert.Equal(100m, day.SystemCash);

        // The declaration was right, so there is no variance.
        Assert.Equal(100m, day.DeclaredTotal);
        Assert.Equal(0m, day.DeclaredVariance);

        // And the ZWG is reported rather than dropped.
        Assert.True(day.HasOtherCurrencies);
        var other = Assert.Single(day.OtherCurrencyTotals);
        Assert.Equal("ZWG", other.Currency);
        Assert.Equal(3000m, other.Gross);
    }

    /// <summary>
    /// Both shops bought, in different currencies. The strike rate counts them both — dropping the
    /// one that paid in the other currency would understate the rep's day to keep the money columns
    /// tidy.
    /// </summary>
    [Fact]
    public async Task Productive_calls_count_every_currency_even_though_the_money_does_not()
    {
        AddDay(plannedCustomers: 10, declaredCash: 100m);
        AddVisit("TUCK01", Utc(8, 30));
        AddVisit("CORNER1", Utc(10, 0));
        AddOfflineSale("OFF-USD", "TUCK01", total: 100m, currency: "USD");
        AddOfflineSale("OFF-ZWG", "CORNER1", total: 3000m, currency: "ZWG");
        await _context.SaveChangesAsync();

        var day = await SingleDayAsync();

        Assert.Equal(2, day.ProductiveCalls);
        Assert.Equal(1.0, day.ProductiveCallRate);
        Assert.Equal(100m, day.SystemTotalSales);
    }

    /// <summary>
    /// Nothing declared, so the currency that took the most stands in. The alternative — the first
    /// sale read — made the label depend on the order rows came back in.
    /// </summary>
    [Fact]
    public async Task With_no_declaration_the_largest_currency_states_the_row()
    {
        AddDay(plannedCustomers: 10);
        AddOfflineSale("OFF-USD", "TUCK01", total: 40m, currency: "USD");
        AddOfflineSale("OFF-ZWG", "CORNER1", total: 3000m, currency: "ZWG");
        await _context.SaveChangesAsync();

        var day = await SingleDayAsync();

        Assert.Equal("ZWG", day.Currency);
        Assert.Equal(3000m, day.SystemTotalSales);
        Assert.Equal("USD", Assert.Single(day.OtherCurrencyTotals).Currency);
    }

    /// <summary>
    /// The period summary folded the same mixture one level up. Two reps on two tills must not be
    /// added together either.
    /// </summary>
    [Fact]
    public async Task The_period_summary_holds_each_currency_apart()
    {
        AddDay(plannedCustomers: 10, declaredCash: 100m);
        AddOfflineSale("OFF-USD", "TUCK01", total: 100m, currency: "USD");
        AddOfflineSale("OFF-ZWG", "CORNER1", total: 3000m, currency: "ZWG");
        await _context.SaveChangesAsync();

        var summary = (await RunAsync()).Summary;

        Assert.Equal(2, summary.TotalsByCurrency.Count);
        Assert.Equal(3000m, summary.TotalsByCurrency.Single(t => t.Currency == "ZWG").Gross);
        Assert.Equal(100m, summary.TotalsByCurrency.Single(t => t.Currency == "USD").Gross);

        // The single-value figure names the currency it is in rather than mixing them.
        Assert.Equal("ZWG", summary.PrimaryCurrency!.Currency);
    }

    /// <summary>
    /// An online sale made at half past ten at night is that day's money. Filing it on the day before
    /// moves it out of the day the rep declares against, and creates a variance on two days at once.
    /// </summary>
    [Fact]
    public async Task An_evening_online_sale_counts_against_the_day_it_was_made()
    {
        AddDay(plannedCustomers: 10);
        AddOnlineSale("ON-LATE", Utc(20, 30), "TUCK01", total: 55m);
        await _context.SaveChangesAsync();

        var day = await SingleDayAsync();

        Assert.Equal(Day, day.TradingDate);
        Assert.Equal(55m, day.SystemTotalSales);
    }

    /// <summary>
    /// The declared figures are the rep's own count, kept deliberately separate from the system's so
    /// the difference between them is reportable rather than reconciled away on entry.
    /// </summary>
    [Fact]
    public async Task The_declared_total_is_compared_against_what_the_system_recorded()
    {
        AddDay(plannedCustomers: 10, declaredCash: 90m);
        AddOfflineSale("OFF-1", "TUCK01", total: 100m, paymentMethod: "Cash");
        await _context.SaveChangesAsync();

        var day = await SingleDayAsync();

        Assert.Equal(90m, day.DeclaredTotal);
        Assert.Equal(-10m, day.DeclaredVariance);
    }

    /// <summary>
    /// A rep who traded without ever opening a day still gets a row. The missing departure record is
    /// itself the finding, and dropping the row hides both it and the money.
    /// </summary>
    [Fact]
    public async Task A_day_that_was_never_opened_still_reports_its_sales()
    {
        AddOfflineSale("OFF-1", "TUCK01", total: 40m);
        await _context.SaveChangesAsync();

        var day = await SingleDayAsync();

        Assert.False(day.HasDayRecord);
        Assert.Equal(40m, day.SystemTotalSales);

        // Nothing was planned, so nothing can have been missed. Zero would read as total failure.
        Assert.Null(day.CallComplianceRate);
    }

    /// <summary>
    /// The summary is what a manager reads first, so it has to agree with the rows beneath it.
    /// </summary>
    [Fact]
    public async Task The_summary_totals_the_rows_it_covers()
    {
        AddDay(plannedCustomers: 10);
        AddVisit("TUCK01", Utc(8, 30));
        AddVisit("CORNER1", Utc(10, 0));
        AddOfflineSale("OFF-1", "TUCK01", total: 40m);
        AddOnlineSale("ON-1", Utc(9, 0), "CORNER1", total: 60m);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(1, report.Summary.DayCount);
        Assert.Equal(10, report.Summary.PlannedCustomerCount);
        Assert.Equal(2, report.Summary.CustomersVisited);
        Assert.Equal(2, report.Summary.ProductiveCalls);
        Assert.Equal(100m, Assert.Single(report.Summary.TotalsByCurrency).Gross);
        Assert.Equal(0.2, report.Summary.CallComplianceRate);
        Assert.Equal(1.0, report.Summary.ProductiveCallRate);
        Assert.Equal(50m, report.Summary.AverageOrderValue);
    }

    /// <summary>
    /// A sale from another system that slipped past the source filter names no rep this report can
    /// speak for. It is dropped rather than guessed at, and must not take the day down with it.
    /// </summary>
    [Fact]
    public async Task A_sale_with_an_unreadable_rep_is_left_out_rather_than_thrown_on()
    {
        AddDay(plannedCustomers: 10);
        AddOfflineSale("OFF-GOOD", "TUCK01", total: 40m);
        AddOfflineSale("OFF-BAD", "TUCK01", total: 500m, createdBy: "system");
        await _context.SaveChangesAsync();

        var day = await SingleDayAsync();

        Assert.Equal(40m, day.SystemTotalSales);
    }

    // --- Helpers ---

    private async Task<DepartureComplianceReportResult> RunAsync()
    {
        var handler = new GetDepartureComplianceReportHandler(_context);

        var result = await handler.Handle(
            new GetDepartureComplianceReportQuery(Day, Day),
            CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }

    private async Task<DepartureComplianceDayDto> SingleDayAsync() =>
        Assert.Single((await RunAsync()).Days);

    private static DateTime Utc(int hour, int minute) =>
        new(Day.Year, Day.Month, Day.Day, hour, minute, 0, DateTimeKind.Utc);

    private void AddDay(int plannedCustomers, decimal? declaredCash = null)
    {
        _context.VanRouteDays.Add(new VanRouteDayEntity
        {
            UserId = Rep,
            Username = "van010",
            TradingDate = Day,
            RouteCode = RouteCode,
            RouteName = "Guruve",
            Territory = "Mashonaland Central",
            TruckRegNo = "AEK 1234",
            DepartedAt = Utc(5, 0),
            PlannedCustomerCount = plannedCustomers,
            DeclaredCash = declaredCash,
            DeclaredCurrency = declaredCash is null ? null : "USD"
        });
    }

    private void AddVisit(string customerCode, DateTime checkInUtc)
    {
        _context.TimesheetEntries.Add(new TimesheetEntryEntity
        {
            Channel = TimesheetChannel.VanSales,
            UserId = Rep,
            Username = "van010",
            CustomerCode = customerCode,
            CustomerName = customerCode,
            CheckInTime = checkInUtc,
            CheckOutTime = checkInUtc.AddMinutes(20)
        });
    }

    private void AddOfflineSale(
        string reference,
        string? routeCustomerCode,
        decimal total,
        string? paymentMethod = "Cash",
        string? createdBy = null,
        string currency = "USD")
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = "KefalosVanSales",
            CardCode = VanAccount,
            CardName = "Van 010",
            RouteCustomerCode = routeCustomerCode,
            RouteCustomerName = routeCustomerCode is null ? null : "Tuck Shop",
            DocDate = Day,
            TotalAmount = total,
            VatAmount = 0m,
            Currency = currency,
            WarehouseCode = VanWarehouse,
            PaymentMethod = paymentMethod,
            AmountPaid = total,
            CreatedBy = createdBy ?? Rep.ToString()
        });
    }

    private void AddOnlineSale(
        string reference,
        DateTime createdAtUtc,
        string? routeCustomerCode,
        decimal total,
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
            RouteCustomerCode = routeCustomerCode,
            RouteCustomerName = routeCustomerCode is null ? null : "Corner Store",
            TotalValue = total,
            Currency = "USD",
            PaymentMethod = paymentMethod,
            Status = ReservationStatus.Confirmed,
            CreatedAt = createdAtUtc,
            ExpiresAt = createdAtUtc.AddHours(1),
            ConfirmedAt = createdAtUtc,
            CreatedBy = Rep.ToString()
        });
    }
}
