using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers;
using ShopInventory.Features.InventoryTransfers.Queries.GetPendingTransfers;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The day bounds on the held-transfer list.
/// </summary>
/// <remarks>
/// They exist so a caller after nothing but a count — the administrator's dashboard asking how many
/// transfers were raised today — can ask for one row and read TotalCount. Paging the table instead
/// would enrich every row it walked, and enrichment opens an approval request per transfer: a read
/// that writes, run over the whole table, on every dashboard load.
///
/// So what is under test is really TotalCount, which has to describe the filtered set rather than
/// the page. The row that lands on the last second of the closing day is the case that decides
/// whether the upper bound is a day or an instant.
/// </remarks>
public sealed class PendingTransferDateFilterTests : IDisposable
{
    private static readonly Guid Viewer = Guid.NewGuid();
    private static readonly DateTime Thursday = new(2026, 8, 6, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public PendingTransferDateFilterTests()
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
    public async Task Without_dates_the_whole_table_is_counted()
    {
        await SeedAsync(Thursday.AddDays(-1), Thursday.AddHours(9), Thursday.AddHours(14));

        var result = await RunAsync();

        Assert.Equal(3, result.TotalCount);
    }

    [Fact]
    public async Task A_single_day_counts_only_what_was_raised_on_it()
    {
        await SeedAsync(
            Thursday.AddHours(-1),      // 23:00 the day before
            Thursday.AddHours(9),
            Thursday.AddHours(14),
            Thursday.AddDays(1));       // midnight the day after

        var result = await RunAsync(from: Thursday, to: Thursday);

        Assert.Equal(2, result.TotalCount);
    }

    [Fact]
    public async Task The_closing_day_is_included_to_its_last_second()
    {
        // The bound is a day, not the instant of its midnight: a transfer raised at 23:59 belongs
        // to the day it was raised on, and an exclusive comparison against the date itself would
        // drop everything but the first second of it.
        await SeedAsync(Thursday.AddDays(1).AddSeconds(-1));

        var result = await RunAsync(from: Thursday, to: Thursday.AddDays(1));

        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task An_open_start_counts_everything_up_to_the_closing_day()
    {
        await SeedAsync(Thursday.AddDays(-30), Thursday.AddHours(9), Thursday.AddDays(1));

        var result = await RunAsync(to: Thursday);

        Assert.Equal(2, result.TotalCount);
    }

    [Fact]
    public async Task The_status_filter_still_applies_inside_the_day()
    {
        // How the dashboard reads "of today's transfers, how many reached SAP".
        await SeedAsync(Thursday.AddHours(9), Thursday.AddHours(10));
        await SeedAsync(PendingInventoryTransferStatuses.Posted, Thursday.AddHours(11));
        await SeedAsync(PendingInventoryTransferStatuses.Posted, Thursday.AddDays(-1));

        var result = await RunAsync(
            status: PendingInventoryTransferStatuses.Posted, from: Thursday, to: Thursday);

        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task A_count_only_read_reports_the_day_rather_than_the_page()
    {
        // One row asked for, five raised. TotalCount is the figure the card draws, so it has to
        // describe the filter and not the page — the whole reason the bounds were added.
        await SeedAsync([.. Enumerable.Range(9, 5).Select(hour => Thursday.AddHours(hour))]);

        var result = await RunAsync(from: Thursday, to: Thursday, pageSize: 1);

        Assert.Equal(5, result.TotalCount);
        Assert.Single(result.Items);
    }

    // ── Helpers ─────────────────────────────────────────

    private async Task<PendingInventoryTransferListResponseDto> RunAsync(
        string? status = "all",
        DateTime? from = null,
        DateTime? to = null,
        int pageSize = 20)
    {
        var handler = new GetPendingTransfersHandler(_context, PassThroughEnricher);
        var result = await handler.Handle(
            new GetPendingTransfersQuery(
                Viewer, status, WarehouseCode: null, MineOnly: false, Page: 1, PageSize: pageSize,
                FromDate: from, ToDate: to),
            default);

        return result.Value;
    }

    /// <summary>
    /// Stands in for the real enricher, which opens an approval request per row. Nothing here turns
    /// on approval progress, and a read that writes would make the counts under test depend on how
    /// often they had been read.
    /// </summary>
    private static readonly IPendingInventoryTransferEnricher PassThroughEnricher =
        StubProxy.For<IPendingInventoryTransferEnricher>((_, args) =>
        {
            var records = (IReadOnlyList<PendingInventoryTransferEntity>)args![0]!;
            return Task.FromResult(records
                .Select(record => PendingInventoryTransferMapper.ToDto(record, includeLines: false))
                .ToList());
        });

    private Task SeedAsync(params DateTime[] createdAtUtc) =>
        SeedAsync(PendingInventoryTransferStatuses.AwaitingApproval, createdAtUtc);

    private async Task SeedAsync(string status, params DateTime[] createdAtUtc)
    {
        foreach (var timestamp in createdAtUtc)
        {
            _context.PendingInventoryTransfers.Add(new PendingInventoryTransferEntity
            {
                FromWarehouse = "WH01",
                ToWarehouse = "WH02",
                PayloadJson = "{}",
                Status = status,
                CreatedByUserId = Viewer,
                CreatedByName = "Tester",
                CreatedByRole = ApplicationRoles.DepotController,
                CreatedAtUtc = timestamp,
                LineCount = 1,
                TotalQuantity = 5m
            });
        }

        await _context.SaveChangesAsync();
    }
}
