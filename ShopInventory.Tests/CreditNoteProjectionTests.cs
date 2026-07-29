using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.Invoices.Queries.GetPodUploadStatus;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

public sealed class CreditNoteProjectionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public CreditNoteProjectionTests()
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
    public async Task Upsert_is_idempotent_and_preserves_line_level_invoice_links()
    {
        var service = CreateService();
        var creditNote = BuildCreditNote(
            cancelled: "tNO",
            new SAPCreditNoteLine
            {
                LineNum = 0,
                BaseType = 13,
                BaseEntry = 101,
                BaseLine = 0,
                LineTotal = 50,
                VatSum = 7.50m,
                CreditReason = "Damaged"
            },
            new SAPCreditNoteLine
            {
                LineNum = 1,
                BaseType = 13,
                BaseEntry = 202,
                BaseLine = 3,
                LineTotal = 25,
                VatSum = 3.75m,
                CreditReason = "Returned"
            });

        await service.UpsertAsync([creditNote]);
        _context.ChangeTracker.Clear();
        await service.UpsertAsync([creditNote]);
        _context.ChangeTracker.Clear();

        var stored = await _context.SapCreditNoteSnapshots
            .Include(snapshot => snapshot.Lines)
            .SingleAsync();

        Assert.False(stored.IsCancelled);
        Assert.Equal(9001, stored.SapDocNum);
        Assert.Equal(2, stored.Lines.Count);
        Assert.Contains(stored.Lines, line => line.BaseEntry == 101 && line.BaseLine == 0);
        Assert.Contains(stored.Lines, line => line.BaseEntry == 202 && line.BaseLine == 3);
    }

    [Fact]
    public async Task Upsert_updates_cancellation_and_removes_lines_no_longer_returned_by_sap()
    {
        var service = CreateService();
        await service.UpsertAsync(
        [
            BuildCreditNote(
                cancelled: "tNO",
                new SAPCreditNoteLine { LineNum = 0, BaseType = 13, BaseEntry = 101, LineTotal = 10 },
                new SAPCreditNoteLine { LineNum = 1, BaseType = 13, BaseEntry = 101, LineTotal = 20 })
        ]);
        _context.ChangeTracker.Clear();

        await service.UpsertAsync(
        [
            BuildCreditNote(
                cancelled: "tYES",
                new SAPCreditNoteLine
                {
                    LineNum = 0,
                    BaseType = 13,
                    BaseEntry = 101,
                    LineTotal = 15,
                    CreditReason = "Adjusted"
                })
        ]);
        _context.ChangeTracker.Clear();

        var stored = await _context.SapCreditNoteSnapshots
            .Include(snapshot => snapshot.Lines)
            .SingleAsync();
        var line = Assert.Single(stored.Lines);

        Assert.True(stored.IsCancelled);
        Assert.Equal(15, line.LineTotal);
        Assert.Equal("Adjusted", line.CreditReason);
    }

    [Fact]
    public void Projection_is_complete_only_when_recent_and_without_a_newer_error()
    {
        var now = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);
        var settings = new CreditNoteSyncSettings { StaleAfterMinutes = 10 };
        var state = new CacheSyncStateEntity
        {
            CacheKey = CreditNoteProjectionSyncService.CacheKey,
            LastSyncedAt = now.AddMinutes(-5)
        };

        Assert.True(GetPodUploadStatusHandler.IsCreditNoteProjectionFresh(state, settings, now));

        state.LastErrorAt = now.AddMinutes(-1);
        Assert.False(GetPodUploadStatusHandler.IsCreditNoteProjectionFresh(state, settings, now));

        state.LastErrorAt = null;
        state.LastSyncedAt = now.AddMinutes(-11);
        Assert.False(GetPodUploadStatusHandler.IsCreditNoteProjectionFresh(state, settings, now));
        Assert.False(GetPodUploadStatusHandler.IsCreditNoteProjectionFresh(null, settings, now));
    }

    private CreditNoteProjectionSyncService CreateService() =>
        new(
            _context,
            StubProxy.Unused<ISAPServiceLayerClient>(),
            Options.Create(new CreditNoteSyncSettings()),
            NullLogger<CreditNoteProjectionSyncService>.Instance);

    private static SAPCreditNote BuildCreditNote(
        string cancelled,
        params SAPCreditNoteLine[] lines) =>
        new()
        {
            DocEntry = 7001,
            DocNum = 9001,
            DocDate = "2026-07-28",
            UpdateDate = "2026-07-28",
            CardCode = "C001",
            CardName = "Projection customer",
            DocCurrency = "USD",
            DocTotal = 100,
            VatSum = 15,
            DocumentStatus = "bost_Open",
            Cancelled = cancelled,
            DocumentLines = lines.ToList()
        };
}
