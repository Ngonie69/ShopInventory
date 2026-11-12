using ErrorOr;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Commands.CorrectVanRouteDayMileage;
using ShopInventory.Features.VanSalesReports.Queries.GetDepartureComplianceReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Telematics;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// An Admin correcting the odometer readings a rep typed on the handset.
/// </summary>
/// <remarks>
/// The readings are corrected on the day itself, because every report reads them from there, and each
/// correction leaves a row holding what the day said before. These tests pin both halves: the reports
/// see the corrected figure, and the rep's own reading is never lost.
/// </remarks>
public sealed class VanRouteDayMileageCorrectionTests : IDisposable
{
    private static readonly Guid Rep = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Admin = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTime Day = new(2026, 11, 2);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanRouteDayMileageCorrectionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.AddRange(
            new User
            {
                Id = Rep,
                Username = "van012",
                Email = "van012@example.com",
                PasswordHash = "x",
                Role = "Sales",
                IsActive = true
            },
            new User
            {
                Id = Admin,
                Username = "office.admin",
                Email = "office.admin@example.com",
                PasswordHash = "x",
                Role = ApplicationRoles.Admin,
                IsActive = true
            });

        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// The reported case: a digit too many on the evening reading. The day takes the corrected figure,
    /// so the distance every report shows is right, and the trail keeps what the rep typed.
    /// </summary>
    [Fact]
    public async Task A_mistyped_closing_reading_is_corrected_and_the_reps_own_is_kept()
    {
        var dayId = await AddDayAsync(starting: 123_400, closing: 1_235_100, closed: true);

        var result = await CorrectAsync(dayId, 123_400, 123_510, expectedStarting: 123_400, expectedClosing: 1_235_100);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal(110, result.Value.KilometresTravelled);

        _context.ChangeTracker.Clear();
        var day = await _context.VanRouteDays.AsNoTracking().SingleAsync(candidate => candidate.Id == dayId);
        Assert.Equal(123_400, day.StartingMileage);
        Assert.Equal(123_510, day.ClosingMileage);

        var correction = await _context.VanRouteDayMileageCorrections.AsNoTracking().SingleAsync();
        Assert.Equal(1_235_100, correction.PreviousClosingMileage);
        Assert.Equal(123_510, correction.ClosingMileage);
        Assert.Equal("Extra digit on the closing reading", correction.Reason);
        Assert.Equal(Admin, correction.CorrectedByUserId);
        Assert.Equal("office.admin", correction.CorrectedByName);

        var summary = result.Value.Correction!;
        Assert.False(summary.StartingCorrected);
        Assert.True(summary.ClosingCorrected);
        Assert.Equal(1_235_100, summary.RecordedClosingMileage);
        Assert.Equal(1, summary.Corrections);
    }

    /// <summary>
    /// The report the Admin corrects from shows the corrected distance, and says the reading was
    /// corrected and what the rep had typed.
    /// </summary>
    [Fact]
    public async Task The_compliance_report_shows_the_corrected_distance_and_the_reps_reading()
    {
        var dayId = await AddDayAsync(starting: 123_400, closing: 1_235_100, closed: true);

        var before = Assert.Single((await ReportAsync()).Days);
        Assert.Equal(1_111_700, before.KilometresTravelled);
        Assert.Null(before.MileageCorrection);

        await CorrectAsync(dayId, 123_400, 123_510, expectedStarting: 123_400, expectedClosing: 1_235_100);

        var after = Assert.Single((await ReportAsync()).Days);
        Assert.Equal(110, after.KilometresTravelled);
        Assert.NotNull(after.MileageCorrection);
        Assert.True(after.MileageCorrection!.ClosingCorrected);
        Assert.Equal(1_235_100, after.MileageCorrection.RecordedClosingMileage);
        Assert.Equal("office.admin", after.MileageCorrection.CorrectedBy);
    }

    /// <summary>
    /// A morning reading typed too high blocks the rep from ending the day, because End Day refuses a
    /// closing reading below the starting one. Correcting it on the open day is what unblocks them.
    /// </summary>
    [Fact]
    public async Task The_morning_reading_can_be_corrected_while_the_day_is_open()
    {
        var dayId = await AddDayAsync(starting: 1_234_000, closing: null, closed: false);

        var result = await CorrectAsync(dayId, 123_400, null, expectedStarting: 1_234_000, expectedClosing: null);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal(123_400, result.Value.StartingMileage);
        Assert.Null(result.Value.ClosingMileage);
    }

    /// <summary>
    /// End Day writes the evening reading over whatever is there, so one put on an open day would be
    /// lost within hours without anyone being told.
    /// </summary>
    [Fact]
    public async Task The_evening_reading_cannot_be_set_on_an_open_day()
    {
        var dayId = await AddDayAsync(starting: 123_400, closing: null, closed: false);

        var result = await CorrectAsync(dayId, 123_400, 123_500, expectedStarting: 123_400, expectedClosing: null);

        Assert.Equal("VanRouteDayMileage.DayStillOpen", result.FirstError.Code);
        Assert.Equal(0, await _context.VanRouteDayMileageCorrections.CountAsync());
    }

    /// <summary>
    /// The page was loaded while the day was open and the rep has since ended it. Saving the page's
    /// blank evening reading would erase the rep's; the save is refused and nothing is written.
    /// </summary>
    [Fact]
    public async Task A_page_read_before_the_rep_ended_the_day_is_refused()
    {
        var dayId = await AddDayAsync(starting: 123_400, closing: 123_520, closed: true);

        var result = await CorrectAsync(dayId, 123_450, null, expectedStarting: 123_400, expectedClosing: null);

        Assert.Equal(ErrorType.Conflict, result.FirstError.Type);
        Assert.Equal("VanRouteDayMileage.Stale", result.FirstError.Code);

        _context.ChangeTracker.Clear();
        var day = await _context.VanRouteDays.AsNoTracking().SingleAsync(candidate => candidate.Id == dayId);
        Assert.Equal(123_400, day.StartingMileage);
        Assert.Equal(123_520, day.ClosingMileage);
        Assert.Equal(0, await _context.VanRouteDayMileageCorrections.CountAsync());
    }

    [Fact]
    public async Task A_correction_needs_a_reason()
    {
        var dayId = await AddDayAsync(starting: 123_400, closing: 1_235_100, closed: true);

        var result = await CorrectAsync(dayId, 123_400, 123_510, 123_400, 1_235_100, reason: "   ");

        Assert.Equal("VanRouteDayMileage.ReasonRequired", result.FirstError.Code);
    }

    [Fact]
    public async Task A_reading_in_below_the_reading_out_is_refused()
    {
        var dayId = await AddDayAsync(starting: 123_400, closing: 1_235_100, closed: true);

        var result = await CorrectAsync(dayId, 123_400, 123_300, 123_400, 1_235_100);

        Assert.Equal("VanRouteDayMileage.ClosingBeforeStarting", result.FirstError.Code);
    }

    [Fact]
    public async Task A_reading_with_a_digit_too_many_is_refused()
    {
        var dayId = await AddDayAsync(starting: 123_400, closing: 1_235_100, closed: true);

        var result = await CorrectAsync(dayId, 123_400, 12_351_000, 123_400, 1_235_100);

        Assert.Equal("VanRouteDayMileage.ReadingOutOfRange", result.FirstError.Code);
    }

    [Fact]
    public async Task Saving_the_readings_the_day_already_holds_is_refused()
    {
        var dayId = await AddDayAsync(starting: 123_400, closing: 123_510, closed: true);

        var result = await CorrectAsync(dayId, 123_400, 123_510, 123_400, 123_510);

        Assert.Equal("VanRouteDayMileage.NothingChanged", result.FirstError.Code);
        Assert.Equal(0, await _context.VanRouteDayMileageCorrections.CountAsync());
    }

    [Fact]
    public async Task A_day_that_is_gone_is_refused()
    {
        var result = await CorrectAsync(9999, 1, 2, null, null);

        Assert.Equal(ErrorType.NotFound, result.FirstError.Type);
    }

    /// <summary>
    /// The rep's evening reading arrives after the morning one was corrected, so the first correction
    /// never saw it. The rep's reading of each is the one before the first correction that changed it.
    /// </summary>
    [Fact]
    public async Task Each_reading_keeps_the_reps_own_through_corrections_made_apart()
    {
        var dayId = await AddDayAsync(starting: 1_234_000, closing: null, closed: false);
        await CorrectAsync(dayId, 123_400, null, expectedStarting: 1_234_000, expectedClosing: null);

        // The rep ends the day with a reading that is itself wrong.
        await CloseDayAsync(dayId, closing: 123_150);

        var result = await CorrectAsync(dayId, 123_400, 123_510, expectedStarting: 123_400, expectedClosing: 123_150);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        var summary = result.Value.Correction!;
        Assert.True(summary.StartingCorrected);
        Assert.Equal(1_234_000, summary.RecordedStartingMileage);
        Assert.True(summary.ClosingCorrected);
        Assert.Equal(123_150, summary.RecordedClosingMileage);
        Assert.Equal(2, summary.Corrections);
    }

    /// <summary>A reading put back to what the rep typed is no longer shown as corrected.</summary>
    [Fact]
    public async Task A_reading_put_back_to_the_reps_is_no_longer_corrected()
    {
        var dayId = await AddDayAsync(starting: 123_400, closing: 123_510, closed: true);
        await CorrectAsync(dayId, 123_400, 123_501, 123_400, 123_510);

        var result = await CorrectAsync(dayId, 123_400, 123_510, 123_400, 123_501, reason: "The rep was right");

        var summary = result.Value.Correction!;
        Assert.False(summary.ClosingCorrected);
        Assert.Equal(2, summary.Corrections);
        Assert.Equal("The rep was right", summary.Reason);
    }

    /// <summary>
    /// The API key calling on its own has no account. The correction is still kept, with no user for
    /// the foreign key to point at, rather than failing on it.
    /// </summary>
    [Fact]
    public async Task A_correction_by_a_caller_with_no_account_is_kept_without_a_user()
    {
        var dayId = await AddDayAsync(starting: 123_400, closing: 1_235_100, closed: true);

        var result = await CorrectAsync(dayId, 123_400, 123_510, 123_400, 1_235_100, actor: Guid.NewGuid());

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        var correction = await _context.VanRouteDayMileageCorrections.AsNoTracking().SingleAsync();
        Assert.Null(correction.CorrectedByUserId);
        Assert.Null(correction.CorrectedByName);
    }

    private async Task<int> AddDayAsync(int? starting, int? closing, bool closed)
    {
        var day = new VanRouteDayEntity
        {
            UserId = Rep,
            Username = "van012",
            TradingDate = Day,
            RouteCode = "CBD",
            RouteName = "CBD Truck",
            DepartedAt = Day.AddHours(5),
            StartingMileage = starting,
            ClosingMileage = closing,
            ReturnedAt = closed ? Day.AddHours(15) : null
        };

        _context.VanRouteDays.Add(day);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        return day.Id;
    }

    /// <summary>What End Day does to the row, without the handset's clock-dependent lookup.</summary>
    private async Task CloseDayAsync(int dayId, int closing)
    {
        var day = await _context.VanRouteDays.AsTracking().SingleAsync(candidate => candidate.Id == dayId);
        day.ClosingMileage = closing;
        day.ReturnedAt = Day.AddHours(15);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task<ErrorOr<VanRouteDayMileageDto>> CorrectAsync(
        int dayId,
        int? starting,
        int? closing,
        int? expectedStarting,
        int? expectedClosing,
        string reason = "Extra digit on the closing reading",
        Guid? actor = null)
    {
        var result = await new CorrectVanRouteDayMileageHandler(
                _context, new NoOpAuditService(), NullLogger<CorrectVanRouteDayMileageHandler>.Instance)
            .Handle(
                new CorrectVanRouteDayMileageCommand(
                    dayId, starting, closing, expectedStarting, expectedClosing, reason, actor ?? Admin),
                CancellationToken.None);

        _context.ChangeTracker.Clear();
        return result;
    }

    private async Task<DepartureComplianceReportResult> ReportAsync()
    {
        var handler = new GetDepartureComplianceReportHandler(
            _context,
            Options.Create(new TaxSettings()),
            new CartrackReadService(_context, Options.Create(new CartrackSettings())));

        var result = await handler.Handle(new GetDepartureComplianceReportQuery(Day, Day), CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }

    /// <summary>The audit trail is not what these tests are about, and it must not fail them.</summary>
    private sealed class NoOpAuditService : IAuditService
    {
        public Task LogAsync(string action, string username, string userRole,
            string? entityType = null, string? entityId = null, string? details = null,
            string? endpoint = null, bool isSuccess = true, string? errorMessage = null) =>
            Task.CompletedTask;

        public Task LogAsync(string action, string? entityType = null, string? entityId = null) =>
            Task.CompletedTask;

        public Task LogAsync(string action, string? entityType, string? entityId, string? details,
            bool isSuccess, string? errorMessage = null) =>
            Task.CompletedTask;
    }
}
