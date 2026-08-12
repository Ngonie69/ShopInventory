using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCompatibility;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesSalesOrderHistory;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The date range a van sales handset asks its history for.
/// </summary>
/// <remarks>
/// The handset sends a seven day span of bare trading days on every online refresh, and means them in
/// CAT. Two things have to be true of what that turns into, and they fail differently.
///
/// <para>
/// The first is a kind. The strings were parsed with <c>AssumeLocal</c>, which stamped the server's own
/// zone onto a value that never carried one, and Npgsql refuses a Local <see cref="DateTime"/> against
/// <c>timestamp with time zone</c> outright. Both history endpoints answered 500 to every refresh a
/// device made until 2026-08-12. SQLite takes a Local kind happily, so no test driving a handler can
/// reproduce that throw — what guards it is asserting the kind of the bounds directly, which is the
/// exact thing the driver inspects.
/// </para>
///
/// <para>
/// The second is where the days fall. Stamping the range UTC instead would satisfy Npgsql and be wrong
/// by the CAT offset in both directions, silently: a sale made at 00:30 would vanish from its own day's
/// history, and one made at 00:30 the morning after the range would appear inside it. Those two cases
/// are what most of this file is about, because nothing about them shows up as an error.
/// </para>
/// </remarks>
public sealed class VanSalesHistoryDateWindowTests : IDisposable
{
    private static readonly Guid Rep = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>The week a handset asks for, as it sends it.</summary>
    private const string WeekStart = "2026-08-06";
    private const string WeekEnd = "2026-08-12";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesHistoryDateWindowTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new ShopInventory.Models.User
        {
            Id = Rep,
            Username = "van-rep",
            PasswordHash = "not-a-real-hash",
            Role = ShopInventory.Models.ApplicationRoles.Merchandiser
        });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── What the driver is handed ───────────────────────────────────────────

    [Fact]
    public void Both_bounds_are_UTC_so_Npgsql_will_take_them()
    {
        // The regression guard for the 500. Npgsql inspects Kind and rejects Local, so this is the
        // assertion that fails if a legacy date ever reaches a timestamptz comparison unconverted.
        var window = VanSalesLegacyDateWindow.Parse(WeekStart, WeekEnd);

        Assert.Equal(DateTimeKind.Utc, window.FromUtc!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, window.ToUtcExclusive!.Value.Kind);
    }

    [Fact]
    public void A_parsed_legacy_date_never_carries_the_servers_own_zone()
    {
        // "2026-08-06" is a trading day, not an instant. Reading it as one in whatever zone the server
        // happens to run in is what put a Local kind on the wire in the first place.
        var parsed = VanSalesCompatibilityMapper.ParseLegacyDate(WeekStart);

        Assert.Equal(DateTimeKind.Unspecified, parsed!.Value.Kind);
    }

    // ── Where the days fall ─────────────────────────────────────────────────

    [Fact]
    public void The_window_opens_two_hours_before_the_trading_day_does()
    {
        // CAT is two hours ahead, so the 6th in the van's clock began at 22:00 on the 5th in the
        // column's. Stamping the date UTC would open the window at 00:00 on the 6th and lose the gap.
        var window = VanSalesLegacyDateWindow.Parse(WeekStart, WeekEnd);

        Assert.Equal(new DateTime(2026, 8, 5, 22, 0, 0, DateTimeKind.Utc), window.FromUtc);
    }

    [Fact]
    public void The_window_closes_at_the_first_instant_of_the_day_after_the_closing_one()
    {
        var window = VanSalesLegacyDateWindow.Parse(WeekStart, WeekEnd);

        Assert.Equal(new DateTime(2026, 8, 12, 22, 0, 0, DateTimeKind.Utc), window.ToUtcExclusive);
    }

    [Fact]
    public void The_trading_days_survive_unconverted_for_SAP()
    {
        // SAP filters DocDate as a calendar date in its own CAT terms. Handing it the UTC instants
        // would move each bound back a day.
        var window = VanSalesLegacyDateWindow.Parse(WeekStart, WeekEnd);

        Assert.Equal(new DateTime(2026, 8, 6), window.FromDate);
        Assert.Equal(new DateTime(2026, 8, 12), window.ToDate);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "not a date")]
    public void An_unreadable_bound_leaves_that_end_of_the_range_open(string? start, string? end)
    {
        var window = VanSalesLegacyDateWindow.Parse(start, end);

        Assert.Null(window.FromUtc);
        Assert.Null(window.ToUtcExclusive);
    }

    // ── The history a rep actually gets back ────────────────────────────────

    [Fact]
    public async Task The_first_two_hours_of_the_opening_day_are_still_that_days_trading()
    {
        // A sale rung up at 00:30 CAT on the 6th is stored at 22:30 on the 5th. This is the order a
        // bare SpecifyKind(Utc) drops, and the rep has no way of knowing it is missing.
        await GivenOrder("SO-DAWN", new DateTime(2026, 8, 5, 22, 30, 0, DateTimeKind.Utc));

        var history = await HandleAsync();

        Assert.Equal("SO-DAWN", Assert.Single(history).PurchaseOrders);
    }

    [Fact]
    public async Task Trading_after_dark_on_the_closing_day_is_included()
    {
        await GivenOrder("SO-LATE", new DateTime(2026, 8, 12, 21, 30, 0, DateTimeKind.Utc));

        var history = await HandleAsync();

        Assert.Equal("SO-LATE", Assert.Single(history).PurchaseOrders);
    }

    [Fact]
    public async Task The_night_before_the_window_is_left_out()
    {
        // 23:30 CAT on the 5th — the evening before the week the handset asked for.
        await GivenOrder("SO-EVE", new DateTime(2026, 8, 5, 21, 30, 0, DateTimeKind.Utc));

        var history = await HandleAsync();

        Assert.Empty(history);
    }

    [Fact]
    public async Task The_morning_after_the_window_is_left_out()
    {
        // 00:30 CAT on the 13th is stored at 22:30 on the 12th, so it sits inside the closing day when
        // the range is read as UTC. The other direction of the same mistake.
        await GivenOrder("SO-NEXT", new DateTime(2026, 8, 12, 22, 30, 0, DateTimeKind.Utc));

        var history = await HandleAsync();

        Assert.Empty(history);
    }

    [Fact]
    public async Task A_weeks_refresh_returns_the_week()
    {
        await GivenOrder("SO-DAWN", new DateTime(2026, 8, 5, 22, 30, 0, DateTimeKind.Utc));
        await GivenOrder("SO-MID", new DateTime(2026, 8, 9, 7, 0, 0, DateTimeKind.Utc));
        await GivenOrder("SO-LATE", new DateTime(2026, 8, 12, 21, 30, 0, DateTimeKind.Utc));
        await GivenOrder("SO-EVE", new DateTime(2026, 8, 5, 21, 30, 0, DateTimeKind.Utc));
        await GivenOrder("SO-NEXT", new DateTime(2026, 8, 12, 22, 30, 0, DateTimeKind.Utc));

        var history = await HandleAsync();

        Assert.Equal(
            ["SO-LATE", "SO-MID", "SO-DAWN"],
            history.Select(order => order.PurchaseOrders));
    }

    // ── The way back out ────────────────────────────────────────────────────

    [Fact]
    public void A_trading_day_from_SAP_is_not_shifted_on_the_way_back_to_the_handset()
    {
        // The parsed value is already in the handset's terms. Converting it to CAT a second time would
        // hand back a trading day that starts at 02:00.
        var mapped = VanSalesCompatibilityMapper.MapLegacyTransferRequest(
            new InventoryTransferRequestDto { DocEntry = 41, DocNum = 41, DocDate = "2026-08-12" },
            status: 0);

        Assert.Equal("2026-08-12 00:00:00", mapped.Date);
    }

    [Fact]
    public void A_stored_instant_is_still_moved_into_CAT_on_the_way_back()
    {
        // The other half of the same call: OrderDate is a real UTC instant and the handset reads
        // everything as CAT, so 07:00 has to arrive as 09:00.
        var mapped = VanSalesCompatibilityMapper.MapLegacySalesOrder(new SalesOrderDto
        {
            Id = 1,
            OrderNumber = "SO-1",
            CardCode = "TMP119",
            OrderDate = new DateTime(2026, 8, 12, 7, 0, 0, DateTimeKind.Utc)
        });

        Assert.Equal("2026-08-12 09:00:00", mapped.DocDate);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<List<VanSalesLegacyOrderDto>> HandleAsync()
    {
        var handler = new GetVanSalesSalesOrderHistoryHandler(
            _context,
            NullLogger<GetVanSalesSalesOrderHistoryHandler>.Instance);

        var result = await handler.Handle(
            new GetVanSalesSalesOrderHistoryQuery(
                Rep,
                new VanSalesOrderSearchRequest { Type = "SO", StartDate = WeekStart, EndDate = WeekEnd }),
            CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }

    private async Task GivenOrder(string orderNumber, DateTime orderDateUtc)
    {
        _context.SalesOrders.Add(new SalesOrderEntity
        {
            OrderNumber = orderNumber,
            CardCode = "TMP119",
            CardName = "Customer TMP119",
            OrderDate = orderDateUtc,
            CreatedAt = orderDateUtc,
            UpdatedAt = orderDateUtc,
            CreatedByUserId = Rep,
            Source = SalesOrderSource.Mobile,
            Status = SalesOrderStatus.Pending,
            IsSynced = false,
            RowVersion = BitConverter.GetBytes(1L)
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    /// <summary>
    /// <see cref="SalesOrderEntity.RowVersion"/> is <c>[Timestamp]</c>, which Npgsql maps to the
    /// store-generated <c>xmin</c> system column. SQLite has no equivalent, so EF leaves the column
    /// out of the INSERT and the NOT NULL constraint fails.
    /// </summary>
    private sealed class SqliteApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<SalesOrderEntity>()
                .Property(order => order.RowVersion)
                .ValueGeneratedNever()
                .IsConcurrencyToken(false);
        }
    }
}
