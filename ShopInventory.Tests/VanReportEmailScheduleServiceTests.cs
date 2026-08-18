using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Data;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers storing and editing a van report email schedule.
///
/// This is the only part of the van suite that causes something to leave the building, so its
/// failures are a different kind from a report's: a report that is wrong misleads one reader, and a
/// schedule that is wrong mails a list of people repeatedly.
///
/// Two behaviours carry that risk and are pinned hardest.
///
/// A new schedule is anchored to the moment it was created, and the anchor is the floor that stops
/// it firing for a period that has already elapsed. Without it, saving a weekly schedule on a Friday
/// would immediately send Monday's report to everybody on the list.
///
/// An edit leaves the anchor and the last-sent marker alone. Re-anchoring on every save would let a
/// schedule that is edited often never quite come due; clearing the last send would make it send
/// again the minute somebody fixed a typo in its name.
/// </summary>
public sealed class VanReportEmailScheduleServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<WebAppDbContext> _options;
    private readonly VanReportEmailScheduleService _service;

    public VanReportEmailScheduleServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<WebAppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var context = new WebAppDbContext(_options))
        {
            context.Database.EnsureCreated();
        }

        _service = new VanReportEmailScheduleService(
            new Factory(_options),
            NullLogger<VanReportEmailScheduleService>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    private sealed class Factory(DbContextOptions<WebAppDbContext> options)
        : IDbContextFactory<WebAppDbContext>
    {
        public WebAppDbContext CreateDbContext() => new(options);
    }

    private static VanReportEmailSchedule New(string name = "Ops — weekly") =>
        new()
        {
            Name = name,
            ReportKind = nameof(VanReportKind.Scorecard),
            Frequency = nameof(ReportScheduleFrequency.Weekly),
            DayOfWeek = (int)DayOfWeek.Monday,
            SendMinuteOfDay = 6 * 60,
            ToRecipients = "ops@example.com",
            Enabled = true
        };

    // --- Creating ---

    /// <summary>
    /// A schedule saved now is anchored now, and that anchor is what stops it firing for a period
    /// that has already gone. Saving must never send.
    /// </summary>
    [Fact]
    public async Task A_new_schedule_is_anchored_to_now_and_has_never_sent()
    {
        var before = DateTime.UtcNow;

        var saved = await _service.SaveScheduleAsync(New(), "tester");

        Assert.True(saved.Id > 0);
        Assert.InRange(saved.AnchorDateUtc, before, DateTime.UtcNow);
        Assert.Null(saved.LastSentUtc);
        Assert.Null(saved.LastError);
        Assert.Equal("tester", saved.CreatedBy);
    }

    /// <summary>
    /// The anchor is not merely stored — it makes the schedule not due. Checked through the cadence
    /// the job actually uses, because a stored value nothing reads would prove nothing.
    /// </summary>
    [Fact]
    public async Task A_new_schedule_is_not_immediately_due()
    {
        var saved = await _service.SaveScheduleAsync(New(), "tester");

        var rule = VanReportEmailJob.ToRule(saved);

        Assert.False(ReportScheduleCadence.IsDue(
            rule,
            PodScheduleTime.NowLocal(),
            saved.LastSentUtc,
            saved.AnchorDateUtc));
    }

    // --- Editing ---

    /// <summary>
    /// An edit changes what the schedule sends, never when it last sent. Clearing the last send
    /// would make a schedule fire again the minute somebody corrected a typo in its name.
    /// </summary>
    [Fact]
    public async Task Editing_a_schedule_leaves_its_anchor_and_last_send_alone()
    {
        var saved = await _service.SaveScheduleAsync(New(), "tester");

        var sentAt = DateTime.UtcNow.AddHours(-3);
        await using (var context = new WebAppDbContext(_options))
        {
            var row = await context.VanReportEmailSchedules.FirstAsync();
            row.LastSentUtc = sentAt;
            row.LastError = "an earlier failure";
            await context.SaveChangesAsync();
        }

        var edit = await _service.GetScheduleAsync(saved.Id);
        Assert.NotNull(edit);

        edit.Name = "Ops — weekly (renamed)";
        edit.ReportKind = nameof(VanReportKind.Margin);

        var updated = await _service.SaveScheduleAsync(edit, "editor");

        Assert.Equal("Ops — weekly (renamed)", updated.Name);
        Assert.Equal(nameof(VanReportKind.Margin), updated.ReportKind);

        // The two fields an edit must not touch.
        Assert.Equal(saved.AnchorDateUtc, updated.AnchorDateUtc);
        Assert.Equal(sentAt, updated.LastSentUtc);
        Assert.Equal("editor", updated.LastModifiedBy);
    }

    // --- Recording a send ---

    /// <summary>
    /// A success moves the marker and clears the error. Leaving a stale error would make a working
    /// schedule look broken for ever.
    /// </summary>
    [Fact]
    public async Task A_successful_send_moves_the_marker_and_clears_the_error()
    {
        var saved = await _service.SaveScheduleAsync(New(), "tester");
        await _service.RecordSendAsync(saved.Id, success: false, "smtp refused it");

        var failed = await _service.GetScheduleAsync(saved.Id);
        Assert.Equal("smtp refused it", failed!.LastError);
        Assert.Null(failed.LastSentUtc);

        await _service.RecordSendAsync(saved.Id, success: true, null);

        var sent = await _service.GetScheduleAsync(saved.Id);
        Assert.NotNull(sent!.LastSentUtc);
        Assert.Null(sent.LastError);
    }

    /// <summary>
    /// A failure records the reason and deliberately does NOT move the marker, so the schedule stays
    /// due and is retried on the next tick. Moving it would turn a transient mail failure into a
    /// silently skipped period.
    /// </summary>
    [Fact]
    public async Task A_failed_send_records_the_reason_and_stays_due()
    {
        var saved = await _service.SaveScheduleAsync(New(), "tester");

        await _service.RecordSendAsync(saved.Id, success: false, "the mail host was unreachable");

        var after = await _service.GetScheduleAsync(saved.Id);

        Assert.Null(after!.LastSentUtc);
        Assert.Equal("the mail host was unreachable", after.LastError);
    }

    /// <summary>An error longer than the column is truncated rather than throwing on save.</summary>
    [Fact]
    public async Task A_very_long_error_is_truncated()
    {
        var saved = await _service.SaveScheduleAsync(New(), "tester");

        await _service.RecordSendAsync(saved.Id, success: false, new string('x', 900));

        var after = await _service.GetScheduleAsync(saved.Id);

        Assert.Equal(500, after!.LastError!.Length);
    }

    // --- Deleting ---

    [Fact]
    public async Task Deleting_removes_the_schedule_and_deleting_again_is_harmless()
    {
        var saved = await _service.SaveScheduleAsync(New(), "tester");

        await _service.DeleteScheduleAsync(saved.Id, "tester");
        Assert.Empty(await _service.GetSchedulesAsync());

        // A second delete — two people on the settings screen at once — must not throw.
        await _service.DeleteScheduleAsync(saved.Id, "tester");
    }

    [Fact]
    public async Task Schedules_come_back_in_name_order()
    {
        await _service.SaveScheduleAsync(New("Zulu"), "tester");
        await _service.SaveScheduleAsync(New("Alpha"), "tester");

        var all = await _service.GetSchedulesAsync();

        Assert.Equal(["Alpha", "Zulu"], all.Select(schedule => schedule.Name).ToArray());
    }
}
