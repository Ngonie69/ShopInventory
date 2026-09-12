using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The convention that stops a non-UTC DateTime reaching a timestamptz column.
///
/// These assert on the model rather than on a round trip, and that is the point. The failure this
/// prevents happens inside Npgsql, which the test suite does not run against — SQLite compares a
/// Local, an Unspecified and a Utc DateTime perfectly happily, which is exactly how two of these bugs
/// reached production through a green build. What can be checked here is that the converter is
/// attached to the properties that need it, attached to none that would be damaged by it, and that it
/// maps each Kind the way the fix depends on.
/// </summary>
public sealed class UtcDateTimeConventionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public UtcDateTimeConventionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void An_instant_column_carries_the_converter()
    {
        // DesktopFiscalTransactions.TimestampUtc is `timestamp with time zone`, and filtering it with a
        // date bound from a query string is what threw.
        var property = Property<DesktopFiscalTransactionEntity>(nameof(DesktopFiscalTransactionEntity.TimestampUtc));

        Assert.NotNull(property.GetValueConverter());
    }

    [Fact]
    public void A_calendar_day_column_does_not()
    {
        // DesktopSales.DocDate is `date`. Converting a local midnight to UTC here would move the row to
        // the previous day — a worse bug than the one being fixed, and silent.
        var property = Property<DesktopSaleEntity>(nameof(DesktopSaleEntity.DocDate));

        Assert.Equal("date", property.GetColumnType());
        Assert.Null(property.GetValueConverter());
    }

    [Fact]
    public void Every_instant_column_in_the_model_carries_it()
    {
        // The whole value of a convention is that it needs no per-site upkeep. A new entity with a new
        // timestamp must be covered the day it is added, without anyone remembering this file.
        var uncovered = _context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Where(property => property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
            .Where(property => !IsCalendarOrWallClock(property.GetColumnType()))
            .Where(property => property.GetValueConverter() is null)
            .Select(property => $"{property.DeclaringType.ShortName()}.{property.Name}")
            .OrderBy(name => name)
            .ToList();

        Assert.Empty(uncovered);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    public void Whatever_kind_goes_in_utc_comes_out(DateTimeKind kind)
    {
        var property = Property<DesktopFiscalTransactionEntity>(nameof(DesktopFiscalTransactionEntity.TimestampUtc));
        var converter = property.GetValueConverter();

        Assert.NotNull(converter);

        var input = DateTime.SpecifyKind(new DateTime(2026, 9, 12, 8, 30, 0), kind);
        var stored = (DateTime)converter.ConvertToProvider(input)!;

        Assert.Equal(DateTimeKind.Utc, stored.Kind);
    }

    [Fact]
    public void A_local_time_converts_to_the_instant_it_names()
    {
        // Local carries an offset, so it means one instant and converting is not a guess.
        var converter = Property<DesktopFiscalTransactionEntity>(
            nameof(DesktopFiscalTransactionEntity.TimestampUtc)).GetValueConverter()!;

        var local = DateTime.SpecifyKind(new DateTime(2026, 9, 12, 8, 30, 0), DateTimeKind.Local);
        var stored = (DateTime)converter.ConvertToProvider(local)!;

        Assert.Equal(local.ToUniversalTime(), stored);
    }

    [Fact]
    public void An_unspecified_time_is_stamped_rather_than_shifted()
    {
        // The deliberate half of the design. Unspecified carries no offset, and reading it as local
        // would move every bare `new DateTime(…)` and every parsed date by the CAT offset — two hours
        // of error in results nobody would ever see. A caller that really means the reader's own day
        // converts before handing the value over; only that caller knows it does.
        var converter = Property<DesktopFiscalTransactionEntity>(
            nameof(DesktopFiscalTransactionEntity.TimestampUtc)).GetValueConverter()!;

        var unspecified = new DateTime(2026, 9, 12, 8, 30, 0, DateTimeKind.Unspecified);
        var stored = (DateTime)converter.ConvertToProvider(unspecified)!;

        Assert.Equal(new DateTime(2026, 9, 12, 8, 30, 0, DateTimeKind.Utc), stored);
        Assert.Equal(unspecified.TimeOfDay, stored.TimeOfDay);
    }

    private static bool IsCalendarOrWallClock(string? columnType) =>
        columnType is not null
        && (columnType.Equals("date", StringComparison.OrdinalIgnoreCase)
            || columnType.Contains("without time zone", StringComparison.OrdinalIgnoreCase)
            || columnType.Equals("timetz", StringComparison.OrdinalIgnoreCase)
            || columnType.Equals("interval", StringComparison.OrdinalIgnoreCase)
            || columnType.StartsWith("time", StringComparison.OrdinalIgnoreCase));

    private IProperty Property<TEntity>(string name) where TEntity : class
    {
        var property = _context.Model.FindEntityType(typeof(TEntity))?.FindProperty(name);
        Assert.NotNull(property);
        return property;
    }
}
