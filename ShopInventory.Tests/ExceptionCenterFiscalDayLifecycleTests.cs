using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.ExceptionCenter;
using ShopInventory.Features.ExceptionCenter.Queries.GetExceptionCenter;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Which fiscal days the Exception Center lists, and in what order.
/// </summary>
/// <remarks>
/// The order is the interesting half. A fiscal day carries two clocks that cannot be ranked against each
/// other: <c>OpenedAtLocal</c> is the taxpayer's wall clock, which is the clock the deadline is measured
/// in and the reason the column exists, while <c>CreatedAt</c> is the UTC instant this server first
/// recorded the day. Ordering on a coalesce of the two asked PostgreSQL to apply one operation across
/// 'timestamp without time zone' and 'timestamp with time zone', which it refuses — see
/// <see cref="ExceptionCenterPostgresTranslationTests"/>, which is where that is caught.
///
/// <para>
/// What is pinned here is the behaviour that replaced it. The days the handset opened are ranked among
/// themselves on the wall clock and come first; a day with no wall clock at all — only a reconciling or
/// failed day can be in that state — follows them, ranked on the instant this server saw it, however early
/// that instant is. The coalesce interleaved the two and so let a UTC instant outrank a wall clock that
/// was genuinely older.
/// </para>
/// </remarks>
public sealed class ExceptionCenterFiscalDayLifecycleTests : IDisposable
{
    /// <summary>Days opened before this have stopped rather than slowed. The taxpayer's clock, no offset.</summary>
    private static readonly DateTime StuckBeforeLocal = new(2026, 8, 15, 0, 0, 0, DateTimeKind.Unspecified);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public ExceptionCenterFiscalDayLifecycleTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
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
    public async Task ListsStoppedDaysOldestWallClockFirstAndNeverOpenedDaysLast()
    {
        // Seeded out of order, and with the two never-opened days recorded earliest of all, so a listing
        // that ranked every day on one clock would put them at the front.
        Seed(deviceId: 4, openedAtLocal: null,
            status: FiscalDayLifecycleStatus.Failed, createdAtUtc: new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc));
        Seed(deviceId: 2, openedAtLocal: new DateTime(2026, 8, 12, 8, 0, 0, DateTimeKind.Unspecified),
            status: FiscalDayLifecycleStatus.Failed, createdAtUtc: new DateTime(2026, 8, 12, 6, 0, 0, DateTimeKind.Utc));
        Seed(deviceId: 3, openedAtLocal: null,
            status: FiscalDayLifecycleStatus.NeedsReconciliation, createdAtUtc: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        Seed(deviceId: 1, openedAtLocal: new DateTime(2026, 8, 10, 8, 0, 0, DateTimeKind.Unspecified),
            status: FiscalDayLifecycleStatus.FileGenerated, createdAtUtc: new DateTime(2026, 8, 10, 6, 0, 0, DateTimeKind.Utc));

        await _context.SaveChangesAsync();

        var items = await GetExceptionCenterHandler.LoadFiscalDayLifecycleFailuresAsync(
            _context, StuckBeforeLocal, 750, CancellationToken.None);

        Assert.Equal(
            [
                "Device 1, fiscal day 1",   // wall clock 10 Aug
                "Device 2, fiscal day 1",   // wall clock 12 Aug
                "Device 3, fiscal day 1",   // never opened, recorded 1 Aug
                "Device 4, fiscal day 1"    // never opened, recorded 5 Aug
            ],
            items.Select(item => item.Reference));
    }

    /// <summary>
    /// A day still moving is normal operation. Only a day that stopped belongs on a queue of work.
    /// </summary>
    [Fact]
    public async Task LeavesOutDaysThatAreFinishedOrStillWithinTheirDay()
    {
        Seed(deviceId: 5, openedAtLocal: new DateTime(2026, 8, 18, 20, 0, 0, DateTimeKind.Unspecified),
            status: FiscalDayLifecycleStatus.Open, createdAtUtc: new DateTime(2026, 8, 18, 18, 0, 0, DateTimeKind.Utc));
        Seed(deviceId: 6, openedAtLocal: new DateTime(2026, 8, 1, 8, 0, 0, DateTimeKind.Unspecified),
            status: FiscalDayLifecycleStatus.Submitted, createdAtUtc: new DateTime(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc));
        Seed(deviceId: 7, openedAtLocal: new DateTime(2026, 8, 2, 8, 0, 0, DateTimeKind.Unspecified),
            status: FiscalDayLifecycleStatus.Closed, createdAtUtc: new DateTime(2026, 8, 2, 6, 0, 0, DateTimeKind.Utc));

        await _context.SaveChangesAsync();

        var items = await GetExceptionCenterHandler.LoadFiscalDayLifecycleFailuresAsync(
            _context, StuckBeforeLocal, 750, CancellationToken.None);

        Assert.Empty(items);
    }

    /// <summary>
    /// Retry is off on every row, which is the point rather than an omission — closing a day twice or
    /// uploading one file twice is not idempotent at FDMS.
    /// </summary>
    [Fact]
    public async Task NeverOffersARetry()
    {
        Seed(deviceId: 8, openedAtLocal: new DateTime(2026, 8, 10, 8, 0, 0, DateTimeKind.Unspecified),
            status: FiscalDayLifecycleStatus.NeedsReconciliation, createdAtUtc: new DateTime(2026, 8, 10, 6, 0, 0, DateTimeKind.Utc));

        await _context.SaveChangesAsync();

        var item = Assert.Single(await GetExceptionCenterHandler.LoadFiscalDayLifecycleFailuresAsync(
            _context, StuckBeforeLocal, 750, CancellationToken.None));

        Assert.False(item.CanRetry);
        Assert.Equal(ExceptionCenterSources.FiscalDayLifecycle, item.Source);
    }

    private void Seed(int deviceId, DateTime? openedAtLocal, FiscalDayLifecycleStatus status, DateTime createdAtUtc)
        => _context.FiscalDayStates.Add(new FiscalDayStateEntity
        {
            DeviceId = deviceId,
            FiscalDayNo = 1,
            OpenedAtLocal = openedAtLocal,
            Status = status,
            CreatedAt = createdAtUtc,
            UpdatedAt = createdAtUtc
        });
}
