using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.InventoryTransfers;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the draft reference a held transfer carries before it reaches SAP, and the line
/// items shown against it.
/// </summary>
/// <remarks>
/// A held transfer has no SAP DocNum, so the draft number is the only reference anyone —
/// approver, depot, printed sheet — can quote for it. It has to be unique and it has to keep
/// counting up rather than restarting, or two different stock movements answer to one number.
/// </remarks>
public sealed class PendingTransferDraftTests : IDisposable
{
    private static readonly DateTime Y2026 = new(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public PendingTransferDraftTests()
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

    // ── Draft numbering ─────────────────────────────────

    [Fact]
    public async Task The_first_draft_of_the_year_starts_the_sequence()
    {
        Assert.Equal("DT-2026-00001", await PendingTransferDraftNumbers.NextAsync(_context, Y2026, default));
    }

    [Fact]
    public async Task Each_draft_takes_the_next_number()
    {
        await AddPendingAsync("DT-2026-00001", Y2026);
        await AddPendingAsync("DT-2026-00002", Y2026);

        Assert.Equal("DT-2026-00003", await PendingTransferDraftNumbers.NextAsync(_context, Y2026, default));
    }

    [Fact]
    public async Task Numbering_restarts_each_year()
    {
        await AddPendingAsync("DT-2026-00001", Y2026);
        await AddPendingAsync("DT-2026-00002", Y2026);

        var nextYear = await PendingTransferDraftNumbers.NextAsync(_context, Y2026.AddYears(1), default);

        Assert.Equal("DT-2027-00001", nextYear);
    }

    [Fact]
    public async Task Drafts_from_other_years_do_not_move_this_years_sequence()
    {
        await AddPendingAsync("DT-2025-00042", Y2026.AddYears(-1));

        Assert.Equal("DT-2026-00001", await PendingTransferDraftNumbers.NextAsync(_context, Y2026, default));
    }

    [Fact]
    public async Task Counting_continues_past_the_point_where_text_and_number_order_could_diverge()
    {
        // The next number is read as the highest by text, so the counter has to stay zero
        // padded to a fixed width: "DT-2026-9" would otherwise sort above "DT-2026-10".
        await AddPendingAsync("DT-2026-00009", Y2026);

        Assert.Equal("DT-2026-00010", await PendingTransferDraftNumbers.NextAsync(_context, Y2026, default));
    }

    [Fact]
    public async Task Records_predating_draft_numbering_do_not_break_allocation()
    {
        await AddPendingAsync(null, Y2026);

        Assert.Equal("DT-2026-00001", await PendingTransferDraftNumbers.NextAsync(_context, Y2026, default));
    }

    [Fact]
    public async Task A_draft_number_cannot_be_issued_twice()
    {
        await AddPendingAsync("DT-2026-00001", Y2026);

        await Assert.ThrowsAsync<DbUpdateException>(() => AddPendingAsync("DT-2026-00001", Y2026));
    }

    [Fact]
    public async Task Unnumbered_records_do_not_collide_with_each_other()
    {
        await AddPendingAsync(null, Y2026);

        var exception = await Record.ExceptionAsync(() => AddPendingAsync(null, Y2026));

        Assert.Null(exception);
    }

    // ── Draft detail ────────────────────────────────────

    [Fact]
    public void The_draft_carries_its_number_and_line_items()
    {
        // The approval queue is served without lines, so the detail mapping is the only place
        // an approver can see what they are signing off on.
        var pending = Pending("DT-2026-00007", Y2026);
        pending.PayloadJson = PendingInventoryTransferMapper.SerializePayload(new CreateInventoryTransferRequest
        {
            FromWarehouse = "WH01",
            ToWarehouse = "WH02",
            Lines =
            [
                new CreateInventoryTransferLineRequest { ItemCode = "ITEM-1", Quantity = 3m, UoMCode = "EA" },
                new CreateInventoryTransferLineRequest { ItemCode = "ITEM-2", Quantity = 4m, FromWarehouseCode = "WH09" }
            ]
        });

        var dto = PendingInventoryTransferMapper.ToDto(pending);

        Assert.Equal("DT-2026-00007", dto.DraftNumber);
        Assert.Equal(2, dto.Lines.Count);
        Assert.Equal("ITEM-1", dto.Lines[0].ItemCode);
        // Lines fall back to the document route when they do not name their own warehouse.
        Assert.Equal("WH01", dto.Lines[0].FromWarehouseCode);
        Assert.Equal("WH09", dto.Lines[1].FromWarehouseCode);
        Assert.Equal("WH02", dto.Lines[1].ToWarehouseCode);
    }

    [Fact]
    public void An_unreadable_payload_still_yields_a_summary()
    {
        var pending = Pending("DT-2026-00008", Y2026);
        pending.PayloadJson = "{ not json";

        var dto = PendingInventoryTransferMapper.ToDto(pending);

        Assert.Equal("DT-2026-00008", dto.DraftNumber);
        Assert.Empty(dto.Lines);
    }

    // ── Helpers ─────────────────────────────────────────

    private static PendingInventoryTransferEntity Pending(string? draftNumber, DateTime createdAtUtc) => new()
    {
        DraftNumber = draftNumber,
        FromWarehouse = "WH01",
        ToWarehouse = "WH02",
        PayloadJson = "{}",
        CreatedByUserId = Guid.NewGuid(),
        CreatedByName = "Tester",
        CreatedByRole = ApplicationRoles.DepotController,
        CreatedAtUtc = createdAtUtc,
        LineCount = 1,
        TotalQuantity = 5m
    };

    private async Task AddPendingAsync(string? draftNumber, DateTime createdAtUtc)
    {
        _context.PendingInventoryTransfers.Add(Pending(draftNumber, createdAtUtc));
        await _context.SaveChangesAsync();
    }
}
