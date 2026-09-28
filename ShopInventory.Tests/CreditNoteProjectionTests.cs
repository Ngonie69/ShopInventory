using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

public sealed class CreditNoteProjectionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly CommandLog _commands = new();

    public CreditNoteProjectionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(_commands)
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

        Assert.True(CreditNoteProjectionFreshness.IsFresh(state, settings, now));

        state.LastErrorAt = now.AddMinutes(-1);
        Assert.False(CreditNoteProjectionFreshness.IsFresh(state, settings, now));

        state.LastErrorAt = null;
        state.LastSyncedAt = now.AddMinutes(-11);
        Assert.False(CreditNoteProjectionFreshness.IsFresh(state, settings, now));
        Assert.False(CreditNoteProjectionFreshness.IsFresh(null, settings, now));
    }

    [Fact]
    public async Task Upsert_projects_the_header_comment_the_list_shows_as_a_reason()
    {
        var creditNote = BuildCreditNote(cancelled: "tNO");
        creditNote.Comments = "Return based on sales order 44170";

        await CreateService().UpsertAsync([creditNote]);
        _context.ChangeTracker.Clear();

        var stored = await _context.SapCreditNoteSnapshots.SingleAsync();
        Assert.Equal("Return based on sales order 44170", stored.Comments);
    }

    [Fact]
    public async Task Upsert_truncates_a_comment_longer_than_the_column()
    {
        var creditNote = BuildCreditNote(cancelled: "tNO");
        creditNote.Comments = new string('x', 400);

        await CreateService().UpsertAsync([creditNote]);
        _context.ChangeTracker.Clear();

        var stored = await _context.SapCreditNoteSnapshots.SingleAsync();
        Assert.Equal(254, stored.Comments!.Length);
    }

    [Fact]
    public async Task List_is_served_from_the_projection_without_reading_sap()
    {
        SeedSnapshot(docEntry: 7001, docNum: 9001, date: new DateTime(2026, 7, 10), total: 120m, vat: 20m,
            documentStatus: "bost_Close", isCancelled: false, comments: "Damaged on delivery");
        SeedSnapshot(docEntry: 7002, docNum: 9002, date: new DateTime(2026, 7, 20), total: 80m, vat: 10m,
            documentStatus: "bost_Open", isCancelled: false, comments: null);
        SeedSnapshot(docEntry: 7003, docNum: 9003, date: new DateTime(2026, 7, 15), total: 40m, vat: 5m,
            documentStatus: "bost_Open", isCancelled: true, comments: "Voided in error");
        // Outside the requested range.
        SeedSnapshot(docEntry: 7004, docNum: 9004, date: new DateTime(2026, 6, 30), total: 10m, vat: 0m,
            documentStatus: "bost_Open", isCancelled: false, comments: null);
        MarkProjectionReadyForReads();
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // The SAP client throws on any call, so reaching it would fail this test rather than slow it.
        var response = await CreateCreditNoteService().GetAllAsync(
            page: 1,
            pageSize: 25,
            fromDate: new DateTime(2026, 7, 1),
            toDate: new DateTime(2026, 7, 31));

        Assert.Equal(3, response.TotalCount);
        Assert.Equal([9002, 9003, 9001], response.CreditNotes.Select(note => note.SAPDocNum).ToList());

        var newest = response.CreditNotes[0];
        Assert.Equal("SAP-CN-9002", newest.CreditNoteNumber);
        Assert.Equal(7002, newest.SAPDocEntry);
        Assert.Equal(CreditNoteStatus.Approved, newest.Status);
        Assert.Equal(80m, newest.DocTotal);
        Assert.Equal(70m, newest.SubTotal);
        Assert.Equal(10m, newest.TaxAmount);
        // Headers only — a detail view fetches the lines per document.
        Assert.Empty(newest.Lines);

        Assert.Equal("Damaged on delivery", response.CreditNotes.Single(note => note.SAPDocNum == 9001).Reason);
        Assert.Equal(CreditNoteStatus.Applied, response.CreditNotes.Single(note => note.SAPDocNum == 9001).Status);
        Assert.Equal(CreditNoteStatus.Cancelled, response.CreditNotes.Single(note => note.SAPDocNum == 9003).Status);
    }

    [Fact]
    public async Task Projection_list_filters_by_the_status_it_derives_and_pages_the_result()
    {
        for (var offset = 0; offset < 5; offset++)
        {
            SeedSnapshot(
                docEntry: 8000 + offset,
                docNum: 9100 + offset,
                date: new DateTime(2026, 7, 1).AddDays(offset),
                total: 10m,
                vat: 0m,
                documentStatus: "bost_Open",
                isCancelled: offset % 2 == 1,
                comments: null);
        }

        MarkProjectionReadyForReads();
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var response = await CreateCreditNoteService().GetAllAsync(
            page: 2,
            pageSize: 2,
            status: CreditNoteStatus.Approved,
            fromDate: new DateTime(2026, 7, 1),
            toDate: new DateTime(2026, 7, 31));

        // Three of the five are not cancelled, so the count reflects the filter, not the table.
        Assert.Equal(3, response.TotalCount);
        Assert.Equal(2, response.TotalPages);
        Assert.Equal([9100], response.CreditNotes.Select(note => note.SAPDocNum).ToList());
    }

    [Fact]
    public async Task Projection_is_not_read_until_its_first_backfill_has_finished()
    {
        MarkProjectionReadyForReads();
        await _context.SaveChangesAsync();

        // Fresh, but only part-way through its first walk of SAP history.
        var checkpoint = _context.SystemConfigs.Single();
        checkpoint.Value = """{"BackfillCompleted":false,"BackfillThroughDate":"2026-03-31T00:00:00Z"}""";
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        Assert.False(await CreateReadyService().IsReadyForReadsAsync());
    }

    [Fact]
    public async Task Projection_is_not_read_when_the_job_is_switched_off()
    {
        MarkProjectionReadyForReads();
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var disabled = new CreditNoteProjectionSyncService(
            _context,
            StubProxy.Unused<ISAPServiceLayerClient>(),
            Options.Create(new CreditNoteSyncSettings { Enabled = false }),
            NullLogger<CreditNoteProjectionSyncService>.Instance);

        Assert.False(await disabled.IsReadyForReadsAsync());
        Assert.True(await CreateReadyService().IsReadyForReadsAsync());
    }

    [Fact]
    public async Task Sweep_fetches_only_the_documents_whose_version_moved_and_rewrites_only_those()
    {
        var sap = new FakeCreditNoteSap();
        sap.Put(SapDocument(7001, total: 100m), "09:00:00");
        sap.Put(SapDocument(7002, total: 50m), "09:30:00");
        SeedSweepCheckpoint();
        var service = CreateSweepService(sap);

        await service.SyncAsync();
        _context.ChangeTracker.Clear();
        Assert.Equal([7001, 7002], Assert.Single(sap.Fetched));
        var firstWrite = await _context.SapCreditNoteSnapshots
            .ToDictionaryAsync(snapshot => snapshot.SapDocEntry, snapshot => snapshot.SyncedAtUtc);

        // 7002 edited later the same day (same UpdateDate, later UpdateTime); 7003 is new.
        sap.Put(SapDocument(7002, total: 45m), "11:15:00");
        sap.Put(SapDocument(7003, total: 20m), "11:20:00");
        sap.Fetched.Clear();

        await service.SyncAsync();
        _context.ChangeTracker.Clear();

        Assert.Equal([7002, 7003], Assert.Single(sap.Fetched));
        var stored = await _context.SapCreditNoteSnapshots
            .Include(snapshot => snapshot.Lines)
            .OrderBy(snapshot => snapshot.SapDocEntry)
            .ToListAsync();
        Assert.Equal([100m, 45m, 20m], stored.Select(snapshot => snapshot.DocTotal).ToList());
        Assert.Equal(new TimeOnly(11, 15), stored[1].SapUpdateTime);
        Assert.Equal(new TimeOnly(11, 20), stored[2].SapUpdateTime);
        Assert.Equal(45m, Assert.Single(stored[1].Lines).LineTotal);
        Assert.Equal(firstWrite[7001], stored[0].SyncedAtUtc);
        Assert.NotEqual(firstWrite[7002], stored[1].SyncedAtUtc);
        Assert.Equal(3, (await _context.CacheSyncStates.SingleAsync()).ItemCount);
    }

    [Fact]
    public async Task A_sweep_with_nothing_new_in_sap_fetches_no_document_and_writes_only_its_own_state()
    {
        var sap = new FakeCreditNoteSap();
        sap.Put(SapDocument(7001, total: 100m), "09:00:00");
        sap.Put(SapDocument(7002, total: 50m), "09:30:00");
        SeedSweepCheckpoint();
        var service = CreateSweepService(sap);
        await service.SyncAsync();
        _context.ChangeTracker.Clear();

        sap.Fetched.Clear();
        sap.Polls = 0;
        _commands.Clear();
        await service.SyncAsync();

        Assert.Equal(1, sap.Polls);
        Assert.Empty(sap.Fetched);
        var writes = _commands.Writes();
        Assert.NotEmpty(writes);
        Assert.All(writes, command => Assert.Contains("\"CacheSyncStates\"", command));
        Assert.DoesNotContain(_commands.Texts, command => command.Contains("COUNT(", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_status_change_that_leaves_update_time_alone_is_still_fetched()
    {
        // Closing a credit note by reconciliation is the case in point: nothing says SAP moves the
        // header's UpdateDate/UpdateTime for it, so the poll compares status and total as well.
        var sap = new FakeCreditNoteSap();
        sap.Put(SapDocument(7001, total: 100m), "09:00:00");
        SeedSweepCheckpoint();
        var service = CreateSweepService(sap);
        await service.SyncAsync();
        _context.ChangeTracker.Clear();

        sap.Put(SapDocument(7001, total: 100m, status: "bost_Close"), "09:00:00");
        sap.Fetched.Clear();
        await service.SyncAsync();
        _context.ChangeTracker.Clear();

        Assert.Equal([7001], Assert.Single(sap.Fetched));
        Assert.Equal("bost_Close", (await _context.SapCreditNoteSnapshots.SingleAsync()).DocumentStatus);
    }

    [Fact]
    public async Task A_version_without_an_update_time_is_fetched_every_pass_but_not_rewritten()
    {
        var sap = new FakeCreditNoteSap();
        sap.Put(SapDocument(7001, total: 100m), updateTime: null);
        SeedSweepCheckpoint();
        var service = CreateSweepService(sap);
        await service.SyncAsync();
        _context.ChangeTracker.Clear();

        sap.Fetched.Clear();
        _commands.Clear();
        await service.SyncAsync();

        Assert.Equal([7001], Assert.Single(sap.Fetched));
        Assert.DoesNotContain(_commands.Writes(), command => command.Contains("SapCreditNote", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_row_written_before_update_time_was_kept_is_fetched_once_then_left_alone()
    {
        // What production holds on the first sweep after deploying: rows with no stored time.
        await CreateService().UpsertAsync([SapDocument(7001, total: 100m)]);
        _context.ChangeTracker.Clear();
        Assert.Null((await _context.SapCreditNoteSnapshots.SingleAsync()).SapUpdateTime);
        _context.ChangeTracker.Clear();

        var sap = new FakeCreditNoteSap();
        sap.Put(SapDocument(7001, total: 100m), "09:00:00");
        SeedSweepCheckpoint();
        var service = CreateSweepService(sap);

        await service.SyncAsync();
        _context.ChangeTracker.Clear();
        Assert.Equal([7001], Assert.Single(sap.Fetched));
        Assert.Equal(new TimeOnly(9, 0), (await _context.SapCreditNoteSnapshots.SingleAsync()).SapUpdateTime);
        _context.ChangeTracker.Clear();

        sap.Fetched.Clear();
        await service.SyncAsync();
        Assert.Empty(sap.Fetched);
    }

    [Fact]
    public async Task Upserting_a_document_sap_returns_unchanged_leaves_its_row_untouched()
    {
        var service = CreateService();
        var creditNote = SapDocument(7001, total: 100m);
        await service.UpsertAsync([creditNote]);
        _context.ChangeTracker.Clear();

        _commands.Clear();
        await service.UpsertAsync([SapDocument(7001, total: 100m)]);

        Assert.Empty(_commands.Writes());
    }

    /// <summary>Records the sync state and checkpoint that let the projection answer reads.</summary>
    private void MarkProjectionReadyForReads()
    {
        _context.CacheSyncStates.Add(new CacheSyncStateEntity
        {
            CacheKey = CreditNoteProjectionSyncService.CacheKey,
            DisplayName = "Credit Notes",
            LastSyncedAt = DateTime.UtcNow.AddMinutes(-1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _context.SystemConfigs.Add(new SystemConfigEntity
        {
            Key = "CreditNoteSync.Checkpoint",
            ValueType = "json",
            Category = "Synchronization",
            IsEditable = false,
            UpdatedAt = DateTime.UtcNow,
            Value = """{"BackfillCompleted":true,"LastUpdateWatermarkDate":"2026-07-30T00:00:00Z"}"""
        });
    }

    private void SeedSnapshot(int docEntry, int docNum, DateTime date, decimal total, decimal vat,
        string documentStatus, bool isCancelled, string? comments)
    {
        _context.SapCreditNoteSnapshots.Add(new SapCreditNoteSnapshotEntity
        {
            SapDocEntry = docEntry,
            SapDocNum = docNum,
            DocDate = DateTime.SpecifyKind(date, DateTimeKind.Utc),
            CardCode = "C001",
            CardName = "Projection customer",
            DocCurrency = "USD",
            Comments = comments,
            DocTotal = total,
            VatSum = vat,
            DocumentStatus = documentStatus,
            IsCancelled = isCancelled,
            LastSeenInSapAtUtc = DateTime.UtcNow,
            SyncedAtUtc = DateTime.UtcNow
        });
    }

    private CreditNoteService CreateCreditNoteService() =>
        new(
            _context,
            StubProxy.Unused<ISAPServiceLayerClient>(),
            StubProxy.Unused<IFiscalizationService>(),
            CreateReadyService(),
            StubProxy.Unused<IStockLedger>(),
            Options.Create(new ShopInventory.Configuration.FiscalisationSettings()),
            NullLogger<CreditNoteService>.Instance);

    private CreditNoteProjectionSyncService CreateReadyService() =>
        new(
            _context,
            StubProxy.Unused<ISAPServiceLayerClient>(),
            Options.Create(new CreditNoteSyncSettings { Enabled = true, StaleAfterMinutes = 10 }),
            NullLogger<CreditNoteProjectionSyncService>.Instance);

    private CreditNoteProjectionSyncService CreateService() =>
        new(
            _context,
            StubProxy.Unused<ISAPServiceLayerClient>(),
            Options.Create(new CreditNoteSyncSettings()),
            NullLogger<CreditNoteProjectionSyncService>.Instance);

    /// <summary>
    /// A checkpoint past its backfill and already reconciled today, so a sweep runs only its
    /// incremental poll.
    /// </summary>
    private void SeedSweepCheckpoint()
    {
        var now = DateTime.UtcNow;
        _context.SystemConfigs.Add(new SystemConfigEntity
        {
            Key = "CreditNoteSync.Checkpoint",
            ValueType = "json",
            Category = "Synchronization",
            IsEditable = false,
            UpdatedAt = now,
            Value = JsonSerializer.Serialize(new
            {
                BackfillCompleted = true,
                LastUpdateWatermarkDate = now.Date,
                LastReconciledAtUtc = now
            })
        });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    private CreditNoteProjectionSyncService CreateSweepService(FakeCreditNoteSap sap) =>
        new(
            _context,
            sap.AsClient(),
            Options.Create(new CreditNoteSyncSettings { Enabled = true }),
            NullLogger<CreditNoteProjectionSyncService>.Instance);

    private static SAPCreditNote SapDocument(int docEntry, decimal total, string status = "bost_Open") =>
        new()
        {
            DocEntry = docEntry,
            DocNum = docEntry + 2000,
            DocDate = "2026-09-28",
            UpdateDate = "2026-09-28",
            CardCode = "C001",
            CardName = "Projection customer",
            DocCurrency = "USD",
            DocTotal = total,
            VatSum = 0,
            DocumentStatus = status,
            Cancelled = "tNO",
            DocumentLines =
            [
                new SAPCreditNoteLine { LineNum = 0, BaseType = 13, BaseEntry = 101, LineTotal = total }
            ]
        };

    /// <summary>
    /// SAP as the sweep sees it: a version poll over every document it holds, and whole documents
    /// fetched by DocEntry. Records each fetch, and fails the test on any other call.
    /// </summary>
    private sealed class FakeCreditNoteSap
    {
        private readonly SortedDictionary<int, (SAPCreditNote Document, string? UpdateTime)> _documents = [];

        public List<List<int>> Fetched { get; } = [];

        public int Polls { get; set; }

        public void Put(SAPCreditNote document, string? updateTime) =>
            _documents[document.DocEntry] = (document, updateTime);

        public ISAPServiceLayerClient AsClient() => StubProxy.For<ISAPServiceLayerClient>((method, args) =>
            method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetCreditNoteVersionsUpdatedSinceAsync) => Poll(),
                nameof(ISAPServiceLayerClient.GetCreditNotesByDocEntriesAsync) =>
                    Fetch(((IEnumerable<int>)args![0]!).ToList()),
                _ => throw new InvalidOperationException($"The sweep was not expected to call {method.Name}.")
            });

        private Task<List<SAPCreditNote>> Poll()
        {
            Polls++;
            return Task.FromResult(_documents.Values
                .Select(entry => new SAPCreditNote
                {
                    DocEntry = entry.Document.DocEntry,
                    UpdateDate = entry.Document.UpdateDate,
                    UpdateTime = entry.UpdateTime,
                    DocumentStatus = entry.Document.DocumentStatus,
                    Cancelled = entry.Document.Cancelled,
                    DocTotal = entry.Document.DocTotal
                })
                .ToList());
        }

        private Task<List<SAPCreditNote>> Fetch(List<int> docEntries)
        {
            Fetched.Add(docEntries.Order().ToList());
            return Task.FromResult(docEntries
                .Where(_documents.ContainsKey)
                .Select(docEntry => _documents[docEntry].Document)
                .ToList());
        }
    }

    /// <summary>Every SQL command the context sends, so a test can say what was written.</summary>
    private sealed class CommandLog : DbCommandInterceptor
    {
        public List<string> Texts { get; } = [];

        public void Clear() => Texts.Clear();

        public List<string> Writes() => Texts
            .Where(text => !text.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .ToList();

        public override DbCommand CommandInitialized(CommandEndEventData eventData, DbCommand result)
        {
            Texts.Add(result.CommandText);
            return result;
        }
    }

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
