using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.ItemVolumeConversions.Commands.SaveItemVolumeConversion;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the conversion factors as stored data rather than as a report input.
/// </summary>
/// <remarks>
/// The two properties that matter are that a code is one row whatever case it is
/// entered in — the report's lookups are ordinal, so "yog143" landing beside
/// "YOG143" would silently halve an item's volume — and that the shipped catalogue
/// is loaded insert-only, so an administrator's correction is not overwritten by
/// the seed on the next restart.
/// </remarks>
public sealed class ItemVolumeConversionMaintenanceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public ItemVolumeConversionMaintenanceTests()
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
    public async Task Saving_a_lower_case_code_updates_the_existing_row_rather_than_adding_a_second()
    {
        var handler = CreateHandler();

        await handler.Handle(Command("YOG143", 0.6m), default);
        await handler.Handle(Command("yog143", 0.75m), default);

        var conversion = Assert.Single(_context.ItemVolumeConversions);
        Assert.Equal("YOG143", conversion.ItemCode);
        Assert.Equal(0.75m, conversion.VolumeFactor);
    }

    [Fact]
    public async Task A_new_factor_records_who_set_it_and_when()
    {
        var handler = CreateHandler();

        var result = await handler.Handle(Command("NEW001", 1.5m, updatedBy: "ngoni"), default);

        Assert.False(result.IsError);
        Assert.Equal("ngoni", result.Value.UpdatedBy);
        Assert.NotNull(result.Value.UpdatedAt);
        Assert.NotEqual(default, result.Value.CreatedAt);
    }

    [Fact]
    public async Task Retiring_a_factor_keeps_the_row()
    {
        var handler = CreateHandler();

        await handler.Handle(Command("YOG143", 0.6m), default);
        var result = await handler.Handle(Command("YOG143", 0.6m, isActive: false), default);

        Assert.False(result.Value.IsActive);
        Assert.Single(_context.ItemVolumeConversions);
    }

    [Fact]
    public void The_shipped_catalogue_has_no_duplicate_codes()
    {
        // The source spreadsheet had four codes listed twice, one of them with two
        // different factors. A duplicate here would be an arbitrary winner at seed
        // time, so the generated list must already be resolved to one row per code.
        var duplicates = ItemVolumeConversionSeedData.Rows
            .GroupBy(row => row.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.Empty(duplicates);
        Assert.All(ItemVolumeConversionSeedData.Rows, row => Assert.True(row.VolumeFactor >= 0));
    }

    [Fact]
    public void The_shipped_catalogue_is_upper_cased_so_the_reports_ordinal_lookup_finds_it()
    {
        Assert.All(
            ItemVolumeConversionSeedData.Rows,
            row => Assert.Equal(row.ItemCode.ToUpperInvariant(), row.ItemCode));
    }

    [Fact]
    public async Task Seeding_never_overwrites_a_factor_an_administrator_has_changed()
    {
        var seeded = ItemVolumeConversionSeedData.Rows.First();

        _context.ItemVolumeConversions.Add(new ItemVolumeConversionEntity
        {
            ItemCode = seeded.ItemCode,
            VolumeFactor = seeded.VolumeFactor + 1m,
            UpdatedBy = "ngoni"
        });
        await _context.SaveChangesAsync();

        await SeedMissingAsync();

        var conversion = await _context.ItemVolumeConversions
            .SingleAsync(row => row.ItemCode == seeded.ItemCode);

        Assert.Equal(seeded.VolumeFactor + 1m, conversion.VolumeFactor);
        Assert.Equal("ngoni", conversion.UpdatedBy);
        Assert.Equal(ItemVolumeConversionSeedData.Rows.Count, await _context.ItemVolumeConversions.CountAsync());
    }

    [Fact]
    public async Task Seeding_twice_adds_nothing_the_second_time()
    {
        await SeedMissingAsync();
        var afterFirst = await _context.ItemVolumeConversions.CountAsync();

        await SeedMissingAsync();

        Assert.Equal(afterFirst, await _context.ItemVolumeConversions.CountAsync());
        Assert.Equal(ItemVolumeConversionSeedData.Rows.Count, afterFirst);
    }

    /// <summary>
    /// Mirrors <c>DbInitializer.SeedItemVolumeConversionsAsync</c>, which cannot be
    /// called directly because it runs migrations against a live provider first.
    /// </summary>
    private async Task SeedMissingAsync()
    {
        var existing = (await _context.ItemVolumeConversions
                .AsNoTracking()
                .Select(conversion => conversion.ItemCode)
                .ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = ItemVolumeConversionSeedData
            .BuildEntities(DateTime.UtcNow)
            .Where(conversion => !existing.Contains(conversion.ItemCode))
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        _context.ItemVolumeConversions.AddRange(missing);
        await _context.SaveChangesAsync();
    }

    private SaveItemVolumeConversionHandler CreateHandler() =>
        new(_context, NullLogger<SaveItemVolumeConversionHandler>.Instance);

    private static SaveItemVolumeConversionCommand Command(
        string itemCode,
        decimal factor,
        bool isActive = true,
        string? updatedBy = null) =>
        new(itemCode, null, factor, null, isActive, updatedBy);
}
